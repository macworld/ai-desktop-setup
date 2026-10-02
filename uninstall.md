# Remove or recover

The downloaded wrapper unpacks the assistant into a temporary protected directory
and removes its temporary payload when the assistant exits. Delete the downloaded
EXE when it is no longer needed. The official desktop client installed by the
assistant is separate: remove it through Windows Settings → Apps if desired.

Configuration changes are transactional and preserve a backup of the previous
account configuration. On interruption, reopen the same assistant and use its
recovery selection and resume action. A committed configuration retries its
completion receipt instead of writing the credentials again. Cancel clears pending
recovery; it does not uninstall the official client or revoke gateway credentials.

For manual restoration, close the desktop client and assistant, retain a copy of
the current configuration, and restore only the matching backup created for that
account and operation. Do not overwrite a newer configuration blindly. Keep auth
files and backups readable only by the intended Windows account. Delete obsolete
sensitive backups after verifying recovery, and rotate gateway credentials with the
issuing service if they may have been exposed.
