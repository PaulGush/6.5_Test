# Pieces of Freight — playtest guide

## For testers (friends)

You need: **Steam installed, running, and logged in.** You do not need to own
anything on Steam — the test builds ride on Steam's free test appid (480).

1. Download the build for your OS from the itch.io page (link from Paul) —
   `win64` on Windows, `linux64` on Linux. Unzip it anywhere.
2. **Start Steam first**, then run `PiecesOfFreight.exe` (Windows) or
   `PiecesOfFreight.x86_64` (Linux — `chmod +x` it if needed).
3. **Keep the game running**, then accept Paul's invite (Steam overlay
   Shift+Tab, or right-click him in your Steam friends list → *Join Game*).
   Accepting an invite while the game is closed will NOT launch it — Steam
   would try to launch the wrong app. Game first, invite second.
4. In-game: WASD to move, you spawn on the dock next to the ship.

### Reporting problems

Say what happened, what you expected, and paste the contents of
`version.txt` (it sits next to the executable) so we know exactly which build
you were on. Screenshots welcome.

Known good flow if joining fails: quit the game, confirm Steam is running,
start the game again, have Paul re-invite.

## For Paul (making a release)

- **Tools > Ship > Build Test Builds (Win + Linux)** — builds both platforms,
  stamps the git-hash version, zips to `Builds/`.
- **Tools > Ship > Build + Push To itch.io** — same build, then `butler push`
  of both platforms (uploads are differential — small after the first one).

One-time setup for pushing:

1. Create the project on [itch.io](https://itch.io/game/new): kind
   *Downloadable*, visibility **Restricted** (gives you a secret URL to share
   with testers; they need no itch account).
2. Install [butler](https://itch.io/docs/butler/) and run `butler login`.
3. Put your `username/game-slug` into `ItchTarget` in
   `Assets/Editor/TestBuildPipeline.cs`.

CI note: `Game.EditorTools.TestBuildPipeline.BuildAllBatch` is the
`-executeMethod` entry point if this ever moves to GitHub Actions.
