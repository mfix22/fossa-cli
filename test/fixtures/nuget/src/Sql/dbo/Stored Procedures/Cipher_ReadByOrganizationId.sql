﻿CREATE PROCEDURE [dbo].[Cipher_ReadByOrganizationId]
    @OrganizationId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON

    SELECT
        *
    FROM
        [dbo].[CipherView]
    WHERE
        [OrganizationId] = @OrganizationId
END