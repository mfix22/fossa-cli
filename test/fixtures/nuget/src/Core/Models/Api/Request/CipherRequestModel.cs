﻿using System;
using System.ComponentModel.DataAnnotations;
using Bit.Core.Utilities;
using Bit.Core.Models.Table;
using Bit.Core.Enums;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Core.Models.Data;
using Bit.Core.Models.Data;
using Newtonsoft.Json.Linq;

namespace Bit.Core.Models.Api
{
    public class CipherRequestModel
    {
        public CipherType Type { get; set; }

        [StringLength(36)]
        public string OrganizationId { get; set; }
        public string FolderId { get; set; }
        public bool Favorite { get; set; }
        [Required]
        [EncryptedString]
        [StringLength(1000)]
        public string Name { get; set; }
        [EncryptedString]
        [StringLength(10000)]
        public string Notes { get; set; }
        public IEnumerable<CipherFieldModel> Fields { get; set; }
        public Dictionary<string, string> Attachments { get; set; }

        public CipherLoginModel Login { get; set; }
        public CipherCardModel Card { get; set; }
        public CipherIdentityModel Identity { get; set; }
        public CipherSecureNoteModel SecureNote { get; set; }

        public CipherDetails ToCipherDetails(Guid userId)
        {
            var cipher = new CipherDetails
            {
                Type = Type,
                UserId = string.IsNullOrWhiteSpace(OrganizationId) ? (Guid?)userId : null,
                OrganizationId = null,
                Edit = true
            };
            ToCipherDetails(cipher);
            return cipher;
        }

        public CipherDetails ToCipherDetails(CipherDetails existingCipher)
        {
            existingCipher.FolderId = string.IsNullOrWhiteSpace(FolderId) ? null : (Guid?)new Guid(FolderId);
            existingCipher.Favorite = Favorite;
            ToCipher(existingCipher);
            return existingCipher;
        }

        public Cipher ToCipher(Cipher existingCipher)
        {
            switch(existingCipher.Type)
            {
                case CipherType.Login:
                    var loginObj = JObject.FromObject(new CipherLoginData(this),
                        new JsonSerializer { NullValueHandling = NullValueHandling.Ignore });
                    loginObj[nameof(CipherLoginData.Uri)]?.Parent?.Remove();
                    existingCipher.Data = loginObj.ToString(Formatting.None);
                    break;
                case CipherType.Card:
                    existingCipher.Data = JsonConvert.SerializeObject(new CipherCardData(this),
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    break;
                case CipherType.Identity:
                    existingCipher.Data = JsonConvert.SerializeObject(new CipherIdentityData(this),
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    break;
                case CipherType.SecureNote:
                    existingCipher.Data = JsonConvert.SerializeObject(new CipherSecureNoteData(this),
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    break;
                default:
                    throw new ArgumentException("Unsupported type: " + nameof(Type) + ".");
            }

            if((Attachments?.Count ?? 0) == 0)
            {
                return existingCipher;
            }

            var attachments = existingCipher.GetAttachments();
            if((attachments?.Count ?? 0) == 0)
            {
                return existingCipher;
            }

            foreach(var attachment in attachments.Where(a => Attachments.ContainsKey(a.Key)))
            {
                attachment.Value.FileName = Attachments[attachment.Key];
            }

            existingCipher.SetAttachments(attachments);
            return existingCipher;
        }

        public Cipher ToOrganizationCipher()
        {
            if(string.IsNullOrWhiteSpace(OrganizationId))
            {
                throw new ArgumentNullException(nameof(OrganizationId));
            }

            return ToCipher(new Cipher
            {
                Type = Type,
                OrganizationId = new Guid(OrganizationId)
            });
        }

        public CipherDetails ToOrganizationCipherDetails(Guid orgId)
        {
            return ToCipherDetails(new CipherDetails
            {
                Type = Type,
                OrganizationId = orgId,
                Edit = true
            });
        }
    }

    public class CipherWithIdRequestModel : CipherRequestModel
    {
        [Required]
        [StringLength(36)]
        public string Id { get; set; }

        public Cipher ToCipher(Guid userId)
        {
            var cipher = ToCipherDetails(userId);
            cipher.Id = new Guid(Id);
            return cipher;
        }
    }

    public class CipherShareRequestModel : IValidatableObject
    {
        [Required]
        public IEnumerable<string> CollectionIds { get; set; }
        [Required]
        public CipherRequestModel Cipher { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if(string.IsNullOrWhiteSpace(Cipher.OrganizationId))
            {
                yield return new ValidationResult("Cipher OrganizationId is required.",
                    new string[] { nameof(Cipher.OrganizationId) });
            }

            if(!CollectionIds?.Any() ?? false)
            {
                yield return new ValidationResult("You must select at least one collection.",
                    new string[] { nameof(CollectionIds) });
            }
        }
    }

    public class CipherCollectionsRequestModel
    {
        [Required]
        public IEnumerable<string> CollectionIds { get; set; }
    }

    public class CipherBulkDeleteRequestModel
    {
        [Required]
        public IEnumerable<string> Ids { get; set; }
    }

    public class CipherBulkMoveRequestModel
    {
        [Required]
        public IEnumerable<string> Ids { get; set; }
        public string FolderId { get; set; }
    }
}
