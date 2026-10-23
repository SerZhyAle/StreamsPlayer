# STREAMS Player for Windows - exchange guide contribution

Prepared for the shared media exchange portal by StreamsPlayer, 2026-10-19 (SP-0224).
These are source sections for the portal owner to integrate in EN/RU/UK together. They describe the current
implementation; they do not claim a published release or portal-owner acceptance.

## Sign in

Open **Settings**, then **Exchange server**. Read a connection string, a connection file,
or a QR picture from a file or the clipboard. Reading fills the form; it does not connect. Check the server,
then enter your login and password and choose **Sign in**. This program only signs in: create an account in
Fast Media Sorter & Organizer for Android or Fast Media Sorter & Sharing for Windows, or ask the server's host.
The server address and port are under **Details**. Camera QR scanning is a separate, parked feature.

## Receive through your account

Enable your exchange server connection to see the account's broadcasts when you open the list. A guest account sees
only what was given to it and does not announce this PC's broadcasts. **Sign out on this device** forgets the
local sign-in and stops this PC's broadcasts for that account. Imported channels stay. If the server cannot be
told, the app says that this device may remain listed until it is removed elsewhere. While the connection is off,
or no account is signed in, the main window shows neither the exchange server button nor **My broadcasts**; turn the
connection on in Settings to bring them back.

## Open the broadcast list

Choose **My broadcasts** in the main window. The list shows the account's broadcasts at once, grouped by the
device that makes them, and keeps itself current while it is open. There is no refresh button, and nothing is
requested from the server while the list is closed. Pick a broadcast and choose **Play** to open it in a window
of its own, or **Add to my channels** to keep it. Video opens in a player window and audio in a small window with
its own volume and stop. Several broadcasts play side by side, and the station playing in the main window keeps
playing; the list says so when this PC holds as many as it can. A guest sign-in is listed under its own heading,
with only what was given to that guest. To open a broadcast from a link, paste it into **Open by link** at the
bottom of the list, or choose **Paste channel** in the main window, without signing in to the server of the
person who gave it. If it asks for a password, enter the password that person gave you. Treat a private
broadcast link as private.

## Broadcast from this PC

In **Settings**, **Library**, **Broadcasting**, allow this PC to broadcast and start the microphone or system
sound. Nothing is captured until you allow broadcasting and start a source. The live indicator offers a stop,
sharing a link or a description file, and a cast to your own device. Use the exchange server of your own account,
or enable local-network listening explicitly. You can limit listeners. Screen and camera capture belong to
SP-0223 and are not described as available here.

## If a connection does not work

Check that the exchange server is enabled and reachable and that the login and password are correct. Expand
**Details** to check the destination. If a broadcast password was changed, ask its broadcaster for the new one.
If a certificate does not match, ask the server's host or broadcaster to confirm the server and provide a new
connection string or description; do not bypass the refusal. The app's **Media exchange** links open the relevant
portal page in Russian or Ukrainian when that is the interface language, and in English for other languages.
Press **Check the connection** - in Settings, in the broadcast list, or on a broadcast that stopped - to run the step-by-step diagnosis. It runs only when you ask, looks at one step at a time from this PC outward, stops at the first step that fails, and says what happened and what to try in the same words the other programs of the family use. The address and port appear only under **Details**, and the log keeps one line of codes with no address, login or password.

Portal entry: [Media exchange](https://serzhyale.github.io/FastMediaSorter_mob_v2/docs/media-exchange-portal/).
Companion contributions: [Russian](streamsplayer.ru.md), [Ukrainian](streamsplayer.uk.md).
