# Privacy

Opening the assistant or previewing a setup code makes no network request. After
you confirm the displayed setup and API addresses, the assistant sends the setup
credential to the setup service chosen by that code. That service supplies the
configuration/API credentials, model catalog and permitted branding under AGSP.
Confirm the service and address before authenticating; a code chooses whom you trust.

The installed client sends API credentials and inference requests to the configured
gateway when you use it. This assistant does not make paid inference requests.
Official desktop packages are downloaded from their official sources under their
own terms; those servers receive ordinary download connection information. The
assistant does not bundle the official proprietary desktop client.

Credentials, local recovery state and configuration backups are sensitive. The
assistant uses the current Windows account's protected storage and access controls;
backups may contain earlier credentials. Diagnostic evidence should be reviewed
before sharing. Do not publish setup codes, auth files, backups or access tokens.
Cancel clears the assistant's pending local recovery state; it does not revoke a
credential at the issuing service. Use that service to revoke or rotate credentials.
