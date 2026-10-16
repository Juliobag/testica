# Firebase setup

The WPF client signs users in with Firebase Authentication email/password and synchronizes only the `testica_authoring` collection in project `testic-24713`. It does not use a service-account key and does not address operational collections.

## Project prerequisites

1. Create the default Cloud Firestore database if it does not exist.
2. Enable **Authentication → Sign-in method → Email/Password** and create an editor account.
3. Compare `firestore.rules.merged` with the existing Firestore rules. It preserves the rules currently in the project and adds access only for `testica_authoring`. If the rules in the console have changed since they were copied, merge only `FirestoreRules.fragment` into the current rules. Then publish the reviewed merged rules.
4. In **Authentication → Users**, copy the editor account's UID. In Firestore, create `testica_sync_editors/{UID}` with the boolean field `active: true`. Only allow-listed UIDs can read or write the authoring collection. The first WPF sync will be authorized after this document exists.

If sign-in reports `INVALID_LOGIN_CREDENTIALS`, confirm that the exact address is listed in **Authentication → Users** and that the account has an Email/Password provider. A Google sign-in account's Gmail password is not automatically a Firebase password. With email enumeration protection enabled, Firebase intentionally reports one generic error for an unknown email or a bad password.

Firestore grants are additive across matching rules. Review the existing rules for broad matches that allow authenticated users to write other collections; this collection-specific rule cannot cancel an existing broad grant. Keep operational data writes denied to the WPF editor accounts.

## Synchronization format

Each document in `testica_authoring` stores `entityType`, `recordId`, `payloadJson`, and `deleted`. The serialized payload mirrors a row from SQLite, including answers and questionnaire-question links. The client tracks the last common content hash in `firebase_sync_state` inside `ctb.sqlite`.

If both local and remote copies change after their last common state, the client reports a conflict and leaves both copies unchanged. Run synchronization again after resolving the conflict on one side. Deletes are synchronized as tombstones so a later sync does not recreate the deleted row.
