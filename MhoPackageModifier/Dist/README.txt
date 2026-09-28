MHO Package Modifier
====================

A Windows tool for modding Marvel Heroes Omega: look inside the game's packages (.upk), change values
(fog, colours, material settings), view and replace textures, view meshes in 3D, export meshes to FBX and
import them back, edit a zone's buildings in Blender, copy materials between packages, and rebuild a
zone's distant view. Every change can be undone.

Requirements
------------
- Windows 10 or 11, 64-bit. Nothing else to install.
- Marvel Heroes Omega installed (the game's ...\UnrealEngine3\MarvelGame\CookedPCConsole folder).
- Blender (free) for the mesh and zone editing workflows.

Install
-------
1. Unzip the "MHO Package Modifier" folder anywhere you can write to (e.g. Documents or C:\Tools).
   Not in Program Files: the app keeps its exports next to itself and updates itself in place.
2. Run MHO_UPK_Mod.exe. It looks for the game in your Steam libraries; if it doesn't find it, set the
   game folder at the top of the window.
3. Press F1 for the manual (every tab and task is explained there).

Keeping your game safe
----------------------
- Close the game before writing: the app refuses to write while it runs.
- Every change has a "Check (Dry Run)" first; nothing touches the game until you confirm.
- The first time a package is changed, its original is kept next to it as <package>.upk.bak (never
  touched again). Backups tab: Undo steps back one change, "Revert Selected to Original" restores
  the .bak.

Updates
-------
At first start the app asks whether it may check GitHub for new versions; nothing goes over the network
before you answer. If you say yes, it checks once a day (Start tab: turn it off, or "Check for Updates"). When one is
out, a link appears at the top right: it downloads the new version, checks it, installs it and restarts.
Your settings, undo history and exports are kept.

Uninstall
---------
Revert any packages you changed (Backups tab), then delete the app folder, and optionally
%APPDATA%\MhoPackageModifier (settings) and %LOCALAPPDATA%\MhoPackageModifier (undo history).

Why Windows may warn you
------------------------
The app is not code-signed (a signing certificate costs money every year; this is a free fan tool).
- Windows SmartScreen may warn on first start: click "More info", then "Run anyway".
- Some antivirus tools flag new unsigned apps. If yours removes files, add an exclusion for the app folder,
  and please report the false positive: https://www.microsoft.com/wdsi/filesubmission
- Download it only from its Nexus Mods page or from GitHub releases:
  https://github.com/leeper48/MHO-UPK-Tools/releases
- To check a download, compare its checksum with the .sha256 file published next to it on GitHub. In
  PowerShell:  Get-FileHash -Algorithm SHA256 .\MHO_Package_Modifier_v<version>.zip
- The source code is public: https://github.com/leeper48/MHO-UPK-Tools

Privacy
-------
The app collects nothing and sends nothing about you or your game. Its only network use is the update
check (only if you allow it at first start): it reads GitHub's public release list, and downloads a
release only when you choose to install it. Settings and undo history stay on your PC
(%APPDATA% and %LOCALAPPDATA%\MhoPackageModifier).

License: MIT (LICENSE.txt). Third-party components: THIRD-PARTY-NOTICES.txt.

Marvel Heroes Omega and its content belong to their owners. This is a fan-made, non-commercial modding
tool. It contains no game packages; its ZoneData folder holds the zone recipes' inputs (distant building
LODs and ground planes baked from the game's own meshes and textures, and a few textures made for it).
