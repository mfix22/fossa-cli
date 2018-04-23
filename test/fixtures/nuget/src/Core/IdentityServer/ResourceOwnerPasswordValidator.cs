﻿using Bit.Core.Models.Api;
using Bit.Core.Models.Table;
using Bit.Core.Enums;
using Bit.Core.Repositories;
using IdentityServer4.Models;
using IdentityServer4.Validation;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Bit.Core.Services;
using System.Linq;
using Bit.Core.Models;
using Bit.Core.Identity;
using Bit.Core.Models.Data;

namespace Bit.Core.IdentityServer
{
    public class ResourceOwnerPasswordValidator : IResourceOwnerPasswordValidator
    {
        private UserManager<User> _userManager;
        private readonly IDeviceRepository _deviceRepository;
        private readonly IDeviceService _deviceService;
        private readonly IUserService _userService;
        private readonly IEventService _eventService;
        private readonly IOrganizationDuoWebTokenProvider _organizationDuoWebTokenProvider;
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IOrganizationUserRepository _organizationUserRepository;
        private readonly IApplicationCacheService _applicationCacheService;
        private readonly CurrentContext _currentContext;

        public ResourceOwnerPasswordValidator(
            UserManager<User> userManager,
            IDeviceRepository deviceRepository,
            IDeviceService deviceService,
            IUserService userService,
            IEventService eventService,
            IOrganizationDuoWebTokenProvider organizationDuoWebTokenProvider,
            IOrganizationRepository organizationRepository,
            IOrganizationUserRepository organizationUserRepository,
            IApplicationCacheService applicationCacheService,
            CurrentContext currentContext)
        {
            _userManager = userManager;
            _deviceRepository = deviceRepository;
            _deviceService = deviceService;
            _userService = userService;
            _eventService = eventService;
            _organizationDuoWebTokenProvider = organizationDuoWebTokenProvider;
            _organizationRepository = organizationRepository;
            _organizationUserRepository = organizationUserRepository;
            _applicationCacheService = applicationCacheService;
            _currentContext = currentContext;
        }

        public async Task ValidateAsync(ResourceOwnerPasswordValidationContext context)
        {
            var twoFactorToken = context.Request.Raw["TwoFactorToken"]?.ToString();
            var twoFactorProvider = context.Request.Raw["TwoFactorProvider"]?.ToString();
            var twoFactorRemember = context.Request.Raw["TwoFactorRemember"]?.ToString() == "1";
            var twoFactorRequest = !string.IsNullOrWhiteSpace(twoFactorToken) &&
                !string.IsNullOrWhiteSpace(twoFactorProvider);

            if(string.IsNullOrWhiteSpace(context.UserName))
            {
                await BuildErrorResultAsync(false, context, null);
                return;
            }

            var user = await _userManager.FindByEmailAsync(context.UserName.ToLowerInvariant());
            if(user == null || !await _userManager.CheckPasswordAsync(user, context.Password))
            {
                await BuildErrorResultAsync(false, context, user);
                return;
            }

            var twoFactorRequirement = await RequiresTwoFactorAsync(user);
            if(twoFactorRequirement.Item1)
            {
                var twoFactorProviderType = TwoFactorProviderType.Authenticator; // Just defaulting it
                if(!twoFactorRequest || !Enum.TryParse(twoFactorProvider, out twoFactorProviderType))
                {
                    await BuildTwoFactorResultAsync(user, twoFactorRequirement.Item2, context);
                    return;
                }

                var verified = await VerifyTwoFactor(user, twoFactorRequirement.Item2,
                    twoFactorProviderType, twoFactorToken);
                if(!verified && twoFactorProviderType != TwoFactorProviderType.Remember)
                {
                    await BuildErrorResultAsync(true, context, user);
                    return;
                }
                else if(!verified && twoFactorProviderType == TwoFactorProviderType.Remember)
                {
                    await Task.Delay(2000); // Delay for brute force.
                    await BuildTwoFactorResultAsync(user, twoFactorRequirement.Item2, context);
                    return;
                }
            }
            else
            {
                twoFactorRequest = false;
                twoFactorRemember = false;
                twoFactorToken = null;
            }

            var device = await SaveDeviceAsync(user, context);
            await BuildSuccessResultAsync(user, context, device, twoFactorRequest && twoFactorRemember);
            return;
        }

        private async Task BuildSuccessResultAsync(User user, ResourceOwnerPasswordValidationContext context,
            Device device, bool sendRememberToken)
        {
            await _eventService.LogUserEventAsync(user.Id, EventType.User_LoggedIn);

            var claims = new List<Claim>();

            if(device != null)
            {
                claims.Add(new Claim("device", device.Identifier));
            }

            var customResponse = new Dictionary<string, object>();
            if(!string.IsNullOrWhiteSpace(user.PrivateKey))
            {
                customResponse.Add("PrivateKey", user.PrivateKey);
            }

            if(!string.IsNullOrWhiteSpace(user.Key))
            {
                customResponse.Add("Key", user.Key);
            }

            if(sendRememberToken)
            {
                var token = await _userManager.GenerateTwoFactorTokenAsync(user,
                    TwoFactorProviderType.Remember.ToString());
                customResponse.Add("TwoFactorToken", token);
            }

            context.Result = new GrantValidationResult(user.Id.ToString(), "Application",
                identityProvider: "bitwarden",
                claims: claims.Count > 0 ? claims : null,
                customResponse: customResponse);
        }

        private async Task BuildTwoFactorResultAsync(User user, Organization organization,
            ResourceOwnerPasswordValidationContext context)
        {
            var providerKeys = new List<byte>();
            var providers = new Dictionary<byte, Dictionary<string, object>>();

            var enabledProviders = new List<KeyValuePair<TwoFactorProviderType, TwoFactorProvider>>();
            if(organization?.GetTwoFactorProviders() != null)
            {
                enabledProviders.AddRange(organization.GetTwoFactorProviders().Where(
                    p => organization.TwoFactorProviderIsEnabled(p.Key)));
            }

            if(user.GetTwoFactorProviders() != null)
            {
                enabledProviders.AddRange(
                    user.GetTwoFactorProviders().Where(p => user.TwoFactorProviderIsEnabled(p.Key)));
            }

            if(!enabledProviders.Any())
            {
                await BuildErrorResultAsync(false, context, user);
                return;
            }

            foreach(var provider in enabledProviders)
            {
                providerKeys.Add((byte)provider.Key);
                var infoDict = await BuildTwoFactorParams(organization, user, provider.Key, provider.Value);
                providers.Add((byte)provider.Key, infoDict);
            }

            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, "Two factor required.",
                new Dictionary<string, object>
                {
                    { "TwoFactorProviders", providers.Keys },
                    { "TwoFactorProviders2", providers }
                });

            if(enabledProviders.Count() == 1 && enabledProviders.First().Key == TwoFactorProviderType.Email)
            {
                // Send email now if this is their only 2FA method
                await _userService.SendTwoFactorEmailAsync(user);
            }
        }

        private async Task BuildErrorResultAsync(bool twoFactorRequest,
            ResourceOwnerPasswordValidationContext context, User user)
        {
            if(user != null)
            {
                await _eventService.LogUserEventAsync(user.Id,
                    twoFactorRequest ? EventType.User_FailedLogIn2fa : EventType.User_FailedLogIn);
            }

            await Task.Delay(2000); // Delay for brute force.
            context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant,
                customResponse: new Dictionary<string, object>
                {{
                    "ErrorModel", new ErrorResponseModel(twoFactorRequest ?
                        "Two-step token is invalid. Try again." : "Username or password is incorrect. Try again.")
                }});
        }

        public async Task<Tuple<bool, Organization>> RequiresTwoFactorAsync(User user)
        {
            var individualRequired = _userManager.SupportsUserTwoFactor &&
                await _userManager.GetTwoFactorEnabledAsync(user) &&
                (await _userManager.GetValidTwoFactorProvidersAsync(user)).Count > 0;

            Organization firstEnabledOrg = null;
            var orgs = (await _currentContext.OrganizationMembershipAsync(_organizationUserRepository, user.Id))
                .Where(o => o.Status == OrganizationUserStatusType.Confirmed).ToList();
            if(orgs.Any())
            {
                var orgAbilities = await _applicationCacheService.GetOrganizationAbilitiesAsync();
                var twoFactorOrgs = orgs.Where(o => OrgUsing2fa(orgAbilities, o.OrganizationId));
                if(twoFactorOrgs.Any())
                {
                    var userOrgs = await _organizationRepository.GetManyByUserIdAsync(user.Id);
                    firstEnabledOrg = userOrgs.FirstOrDefault(
                        o => orgs.Any(om => om.OrganizationId == o.Id) && o.TwoFactorIsEnabled());
                }
            }

            return new Tuple<bool, Organization>(individualRequired || firstEnabledOrg != null, firstEnabledOrg);
        }

        private bool OrgUsing2fa(IDictionary<Guid, OrganizationAbility> orgAbilities, Guid orgId)
        {
            return orgAbilities != null && orgAbilities.ContainsKey(orgId) &&
                orgAbilities[orgId].Enabled && orgAbilities[orgId].Using2fa;
        }

        private Device GetDeviceFromRequest(ResourceOwnerPasswordValidationContext context)
        {
            var deviceIdentifier = context.Request.Raw["DeviceIdentifier"]?.ToString();
            var deviceType = context.Request.Raw["DeviceType"]?.ToString();
            var deviceName = context.Request.Raw["DeviceName"]?.ToString();
            var devicePushToken = context.Request.Raw["DevicePushToken"]?.ToString();

            if(string.IsNullOrWhiteSpace(deviceIdentifier) || string.IsNullOrWhiteSpace(deviceType) ||
                string.IsNullOrWhiteSpace(deviceName) || !Enum.TryParse(deviceType, out DeviceType type))
            {
                return null;
            }

            return new Device
            {
                Identifier = deviceIdentifier,
                Name = deviceName,
                Type = type,
                PushToken = string.IsNullOrWhiteSpace(devicePushToken) ? null : devicePushToken
            };
        }

        private async Task<bool> VerifyTwoFactor(User user, Organization organization, TwoFactorProviderType type,
            string token)
        {
            switch(type)
            {
                case TwoFactorProviderType.Authenticator:
                case TwoFactorProviderType.Duo:
                case TwoFactorProviderType.YubiKey:
                case TwoFactorProviderType.U2f:
                case TwoFactorProviderType.Remember:
                    if(type != TwoFactorProviderType.Remember && !user.TwoFactorProviderIsEnabled(type))
                    {
                        return false;
                    }
                    return await _userManager.VerifyTwoFactorTokenAsync(user, type.ToString(), token);
                case TwoFactorProviderType.Email:
                    if(!user.TwoFactorProviderIsEnabled(type))
                    {
                        return false;
                    }
                    return await _userService.VerifyTwoFactorEmailAsync(user, token);
                case TwoFactorProviderType.OrganizationDuo:
                    if(!organization?.TwoFactorProviderIsEnabled(type) ?? true)
                    {
                        return false;
                    }

                    return await _organizationDuoWebTokenProvider.ValidateAsync(token, organization, user);
                default:
                    return false;
            }
        }

        private async Task<Dictionary<string, object>> BuildTwoFactorParams(Organization organization, User user,
            TwoFactorProviderType type, TwoFactorProvider provider)
        {
            switch(type)
            {
                case TwoFactorProviderType.Duo:
                case TwoFactorProviderType.U2f:
                case TwoFactorProviderType.Email:
                case TwoFactorProviderType.YubiKey:
                    if(!user.TwoFactorProviderIsEnabled(type))
                    {
                        return null;
                    }

                    var token = await _userManager.GenerateTwoFactorTokenAsync(user, type.ToString());
                    if(type == TwoFactorProviderType.Duo)
                    {
                        return new Dictionary<string, object>
                        {
                            ["Host"] = provider.MetaData["Host"],
                            ["Signature"] = token
                        };
                    }
                    else if(type == TwoFactorProviderType.U2f)
                    {
                        return new Dictionary<string, object>
                        {
                            ["Challenges"] = token
                        };
                    }
                    else if(type == TwoFactorProviderType.Email)
                    {
                        return new Dictionary<string, object>
                        {
                            ["Email"] = RedactEmail((string)provider.MetaData["Email"])
                        };
                    }
                    else if(type == TwoFactorProviderType.YubiKey)
                    {
                        return new Dictionary<string, object>
                        {
                            ["Nfc"] = (bool)provider.MetaData["Nfc"]
                        };
                    }
                    return null;
                case TwoFactorProviderType.OrganizationDuo:
                    if(await _organizationDuoWebTokenProvider.CanGenerateTwoFactorTokenAsync(organization))
                    {
                        return new Dictionary<string, object>
                        {
                            ["Host"] = provider.MetaData["Host"],
                            ["Signature"] = await _organizationDuoWebTokenProvider.GenerateAsync(organization, user)
                        };
                    }
                    return null;
                default:
                    return null;
            }
        }

        private static string RedactEmail(string email)
        {
            var emailParts = email.Split('@');

            string shownPart = null;
            if(emailParts[0].Length > 2 && emailParts[0].Length <= 4)
            {
                shownPart = emailParts[0].Substring(0, 1);
            }
            else if(emailParts[0].Length > 4)
            {
                shownPart = emailParts[0].Substring(0, 2);
            }
            else
            {
                shownPart = string.Empty;
            }

            string redactedPart = null;
            if(emailParts[0].Length > 4)
            {
                redactedPart = new string('*', emailParts[0].Length - 2);
            }
            else
            {
                redactedPart = new string('*', emailParts[0].Length - shownPart.Length);
            }

            return $"{shownPart}{redactedPart}@{emailParts[1]}";
        }

        private async Task<Device> SaveDeviceAsync(User user, ResourceOwnerPasswordValidationContext context)
        {
            var device = GetDeviceFromRequest(context);
            if(device != null)
            {
                var existingDevice = await _deviceRepository.GetByIdentifierAsync(device.Identifier, user.Id);
                if(existingDevice == null)
                {
                    device.UserId = user.Id;
                    await _deviceService.SaveAsync(device);
                    return device;
                }

                return existingDevice;
            }

            return null;
        }
    }
}
