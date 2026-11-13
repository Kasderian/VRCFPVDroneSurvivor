# Dodge Robot World

A cooperative PvE shooter for VRChat. Up to 6 players cross a 500 x 500 m map from Point A to Point B while a swarm of networked flying robots hunts them.

Built with Unity and UdonSharp, this project features AI-driven drones, realistic ballistic bullets, two-handed VR firearms, a stamina system, and a difficulty system that scales the whole threat, all synchronized across players.

<!-- Replace with your published world link -->
WORLD LINK HERE

---

## Screenshots

<!-- Add your own screenshots, for example: -->
<!-- ![World Thumbnail](ReadMeImages/thumbnail.png) -->
<!-- ![Chaser dive](ReadMeImages/chaser.png) -->

---

## Overview

Dodge Robot World is a VRChat world built around one question: can you reach the other side of the map without being spotted, shot or blown up?

Players join the game from an in-world canvas, grab a rifle or shotgun at the start area, and move from Point A to Point B across forest, fields, trenches and buildings. Robots are spawned in waves based on where players have been seen, and they get more numerous as the difficulty goes up. Everyone has 1 HP, and so does every robot.

The project focuses on AI behavior, multiplayer synchronization, and a lightweight, optimized ballistic system within the limits of VRChat's Udon environment.

---

## Features

- **Three robot types** – Chasers (FPV-style kamikaze drones), Droppers (high-altitude bombers) and Spotters (fixed-wing scouts).
- **Difficulty 0-5** – Set from a world-space canvas by the Game Host. 0 stops the game and resets every robot.
- **Game Host system** – Anyone can claim the Game Host role. The Game Host's client runs all robot AI, everyone else only displays.
- **Player roster** – Up to 6 playing players. Everyone else is an observer and is ignored by robots.
- **Realistic ballistics** – Bullets simulated with gravity and a raycast per frame, no script per bullet. Shotgun pellets lose speed with distance.
- **Two-handed firearms** – Rifles and shotguns share one script. Rifles are full-auto, shotguns fire a fixed 9-pellet spread.
- **Ammo and reload** – Magazine and chamber capacity, reload times, and VR or desktop reload input.
- **Swappable sights** – Iron, holographic and a zoomable scope. Shoot a sight cube to change the sight on the gun you are holding.
- **Stamina system** – Running drains stamina and exhaustion forces you to walk. Observers run freely.
- **Death and revival** – Killed players go to a cemetery and come back after a timer or by walking into a revive zone.
- **Minimap** – A top-down camera that renders on a timer instead of every frame.
- **Network optimized** – Spotters need almost no network traffic, and bullets are never synced one by one.

---

## The Robots

### Chaser
A quadcopter that hunts players by sight. It flies on the NavMesh, patrols in a growing clockwise ring, and chases the last seen position of a player. Outdoors it commits to a fast dive at the player's feet. When it cannot dive (indoors, too close, no clear line) it rushes the player and detonates next to them.

States: `Parked`, `Travel`, `Patrol`, `Chase`, `Dive`, `Exploded`.

Notable behavior:
- Looks ahead for ceilings and door lintels and drops to a low flight height indoors.
- Flies through windows and over obstacles using NavMeshLinks.
- The blast kills inside a core radius through walls, and further out only with line of sight.
- Tilts like a small helicopter, including banking into turns.

### Dropper
A high-flying bomber that flies in a straight line (no NavMesh). It travels and patrols at 75 m, descends to 30 m above a spotted player, matches their velocity, and releases bombs when the player is not running. Bombs have no collider, so bullets cannot hit them, and every client simulates their fall from the synced start values. Once out of bombs it climbs to 50 m and circles the player as a close-range spotter. A shot Dropper only explodes visually.

States: `Parked`, `Travel`, `Patrol`, `Attack`, `Trail`, `Exploded`.

### Spotter
A fixed-wing scout flying a smoothed loop at 100 m. Its position is a pure function of server time, so nothing about its movement is networked. Each Spotter watches one area at a time and casts rays from its eye at four body points (head, chest, groin, shin). A shared meter per player fills faster when the player is closer, moving faster, or more exposed. A full meter reports the player's position to the Captain. A shot Spotter disappears for a cooldown and returns on its slot. The only synced data is a cooldown timestamp per Spotter.

### OpFor Captain
The manager that decides where robots go. It stores the last spotted position of each player (the "intel"), targets the player closest to Point B, re-sends free robots, and launches waves from a random LaunchArea. Intel is wiped when a player dies, leaves, reaches Point B, or when it expires. Nothing launches until someone is spotted.

| Difficulty | Chasers (max) | Droppers (max) | Spotters |
|---|---|---|---|
| 0 | game stopped | game stopped | 0 |
| 1 | 2 | 1 | 2 |
| 2 | 4 | 2 | 2 |
| 3 | 6 | 3 | 3 |
| 4 | 8 | 4 | 4 |
| 5 | 10 | 5 | 5 |

---

## How to Play

Open this world in VRChat: WORLD LINK HERE

1. Use the world canvas to press **Join** and enter the game (max 6 players). Everyone else stays an observer.
2. Press **Claim Host** to become the Game Host. Only the Game Host can change the difficulty.
3. Pick a difficulty from 1 to 5 (0 stops the game).
4. Grab a rifle or shotgun at the start area. Shoot one of the three sight cubes to choose iron sights, holographic or scope.
5. Reload with the trigger on the magazine port (VR) or **F** (desktop).
6. Get from Point A to Point B without being spotted or killed.

If you die you are sent to the cemetery. You come back after 30 seconds, or sooner by walking into the revive zone near Point A.

---

## Gameplay Systems

### Firearms
Rifles are full-auto with a 31-round capacity (30 per reload, 3 s reload). Shotguns hold 9 shells, load one per reload (0.5 s each) and fire a fixed ring of pellets. Both are the same `TwoHandGun` script with different inspector values. The guns cannot fire while reloading, and ammo resets when someone else picks the gun up.

### Ballistics
`BallisticManager` steps every active bullet once per frame with gravity and a raycast per segment. Hits are decided on the shooter's client, and the target decides what to network.

### Stamina
Running drains stamina nonlinearly, standing still recovers it fastest, and an exhausted player is limited to walking speed until they recover. A head-following world-space HUD works in both VR and desktop.

### Multiplayer
Every robot, gun and player system is designed for multiple players:

- The **Game Host's client** runs all robot AI.
- The **shooter's client** decides whether a bullet hit a robot.
- Each **player's own client** decides whether they were caught in a blast or touched by a drone, so there is no latency unfairness for the victim.
- Robots that fly freely use VRC Object Sync plus a small Manual-sync child object for their state. Spotters use server time instead.

---

## Technical Details

| Component | Technology |
|---|---|
| Game Engine | Unity |
| Platform | VRChat |
| Scripting | UdonSharp / C# |
| Networking | VRChat Udon Networking, VRC Object Sync |
| Robot Navigation | Unity NavMesh and NavMeshLinks (Chasers) |
| UI | Unity UI / TextMeshPro (world-space canvases) |
| Scope | Second Camera + Render Texture |

---

## Project Structure

<!-- Update this section to match the actual repository structure -->

```text
Assets/
├── Animation/
├── Materials/
├── Prefabs/
├── Scenes/
├── Scripts/
├── Sounds/
└── Terrain/
```

### Scripts

| Script | Purpose |
|---|---|
| `GameManager` | Synced difficulty (0-5) and the Game Host role |
| `DifficultyUI` | Canvas buttons for difficulty and Claim Host |
| `PlayerRoster` / `PlayerSlot` | The 6 playing slots, Join/Leave, alive flag |
| `LocalPlayerState` | Local player's playing/alive state, Kill and Revive |
| `StaminaSystem` | Stamina, movement speeds and HUD |
| `ReviveZone` | Trigger that brings a dead player back |
| `TwoHandGun` | Rifle and shotgun: aim, fire, ammo, reload, sights |
| `GunReloadZone` | VR reload relay on the magazine collider |
| `GunSightSelect` | Shootable cubes that change the held gun's sight |
| `Scope` | Scope zoom from the slider |
| `BallisticManager` | Bullet and pellet simulation and hit detection |
| `OpForCaptain` | Intel, targeting and wave launching |
| `DroneChaser` / `DroneNetSync` | Chaser AI and its synced variables |
| `DroneDropper` / `DropperNetSync` | Dropper AI, bombs and its synced variables |
| `SpotterManager` / `SpotterHit` | All Spotters: movement, vision and being shot |
| `MapCameraRender` | Low-frequency minimap rendering |
| `PickupResetter` | Resets pickups (guns) to their start position |
| `TeleportOnInteract` | Simple teleport on Interact |
| `TestTarget` | Test cube for hit detection |

---

## Development Environment

<!-- Verify these match your current project -->

- Unity 2022.3.22f1
- VRChat SDK - Base 3.10.5
- VRChat SDK - Worlds 3.10.5
- VRChat Package Resolver Tool 0.1.29

---

## Installation

1. Clone or download this repository.
2. Open the project using the compatible Unity version.
3. Ensure the required VRChat SDK and UdonSharp packages are installed.
4. Open the main world scene.
5. Bake the NavMesh (needed by the Chasers).
6. Build and test using the VRChat SDK.

### Setup notes

- **Layers:** Robot hit boxes sit on layer 30 `OpFor`. Tick it in `BallisticManager`'s Hit Mask, and leave it out of the drones' obstacle masks.
- **BallisticManager Hit Mask:** Default, Environment and robot layers. Not Pickup, Player, PlayerLocal or UI.
- **Spotter vision mask:** Only the Default layer, so pickups, players and robots never block the rays.
- **Chaser and Dropper obstacle mask:** Walls, terrain, buildings and trees. Exclude player layers.
- **NavMeshLinks:** Place them on both sides of a window or obstacle so Chasers can pass through or climb over.
- **Scene objects:** Point B, two or more LaunchAreas, Spotter waypoints (children of one parent) and Spotter areas (children of another parent, X scale = radius).

---

## Configuration

Most behavior is tuned through the exposed Unity Inspector settings.

Examples include:

- Robots per wave, wave cooldown and robot respawn time (`OpForCaptain`).
- Flight heights, speeds, vision and blast radii (`DroneChaser`, `DroneDropper`).
- Minimum Spotter count, flight speed, shot cooldown and spotting speed (`SpotterManager`).
- Fire rate, muzzle velocity, ammo capacity, reload time and shotgun pellets (`TwoHandGun`).
- Auto-revive time and cemetery location (`LocalPlayerState`).

Each robot script also has debug options (ignore vision, kill any target, state logging) that must be set in the editor before building.

---

## Possible Future Updates????

- [ ] Waiter robot: waits at a random spot and dives when a player enters its vision.
- [ ] Crasher robot: a fixed-wing that flies in from a launch area and crashes on a called location.
- [ ] Window and roof height markers for Chasers using the NavMeshLink positions.
- [ ] Reaching Point B unlocks a two-way teleporter between A and B.
- [ ] Full load test with all robots and all guns active.
- [ ] Code clean up and more code comments.

---

## Known Issues

<!-- Document known bugs, limitations, or compatibility issues -->

---

## Credits

Developed by Kasder [Kasderian]. Someone took the GitHub Username Kasder already, lol.

My VRChat friends, thank you.

With the help of Claude.

---

## Third-Party Assets & Resources

<!-- List any third-party assets, sounds or models you used, with links -->

---

## License

<!-- Specify your chosen license here -->
Credit me and use it as how you like. Please don't break any laws or any rules to Unity and VRChat.
