# Remove or recover

The downloaded wrapper unpacks the assistant into a temporary protected directory
and removes its temporary payload when the assistant exits. Delete the downloaded
EXE when it is no longer needed. The official desktop client installed by the
assistant is separate: remove it through Windows Settings → Apps if desired.

Configuration changes are transactional and preserve a backup of the previous
account configuration. After a failed operation, use the recovery selection and
resume action. After a crash or unexpected process interruption, reopen the
assistant on the same Windows account to use retained recovery. A committed
configuration retries its completion receipt instead of writing credentials again.
Cancel or closing the assistant normally clears pending local recovery; installed
clients, configuration, backups, and gateway credentials remain.

For manual restoration, close the desktop client and assistant, retain a copy of
the current configuration, and restore only the matching backup created for that
account and operation. Do not overwrite a newer configuration blindly. Keep auth
files and backups readable only by the intended Windows account. Delete obsolete
sensitive backups after verifying recovery, and rotate gateway credentials with the
issuing service if they may have been exposed.
