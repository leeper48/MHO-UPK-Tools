MHO Extended Mod Manager
========================

A mod manager for Marvel Heroes Omega. It installs, turns on and off, orders and applies mods in the MHModManager
format (packages, icons, store images, strings, sound packs), and adds: verified original game files with undo,
updating mods in place, automatic and your own tags, notes, search / sort / group, locking mods at the top or bottom of
the order, PNG import and export for icons, more icon packages, and Nexus Mods update checks. Mods it saves or exports
still install in MHModManager 1.0.1 (there is also a legacy export).

Requirements
  Windows 10 or 11 (64-bit) and Microsoft's .NET 8 Desktop Runtime (x64):
  https://dotnet.microsoft.com/download/dotnet/8.0  ("Windows Desktop Runtime 8", x64)

Install
  Setup.exe: run it (no administrator rights needed; it installs for your user).
  Zip: unzip the "MHO Extended Mod Manager" folder anywhere you can write to, e.g. C:\Games. Not in Program Files and
  not in the game folder: the manager keeps your mods and settings in a "data" folder next to itself.
  On the first start it finds the game (Steam) and offers to bring over an existing MHModManager library.

Updates
  Settings -> Check for Updates. Updates come from https://github.com/leeper48/MHO-UPK-Tools/releases and are checked
  (SHA-256) before anything is replaced; your data folder is never touched. Since 0.37.78 every update is also signed
  with the developer's own key, and the app installs only an update whose signature matches the key built into it.

Close the game before applying changes. Every change to a game file is made from a verified original and can be undone.

Why Windows may warn you (the program is not code-signed)
  This is a free, open-source hobby tool and its files are not signed with a paid certificate. So Windows SmartScreen may
  say "Windows protected your PC" (click "More info", then "Run anyway"), and an antivirus may flag a new version until
  it has been seen more often. To check that your download is the real one:
    - Get it from the official pages only: Nexus Mods or https://github.com/leeper48/MHO-UPK-Tools/releases
    - Compare its SHA-256 with the .sha256 file published next to it. In PowerShell:
        Get-FileHash .\MHO_Ext_ModManager-<version>-Setup.exe -Algorithm SHA256
    - The full source code is public, and every release is built from it on GitHub.
  Malwarebytes may flag it as "Malware.AI.<number>". That is its AI guess for new programs it hasn't seen before and
  that aren't code-signed, not a match with any known malware. Check the SHA-256 as above, then restore the file from
  Malwarebytes' Quarantine and add the program's folder to its Allow List. Each new version can be flagged again until
  Malwarebytes has seen it; reporting it as a false positive (in Malwarebytes or on its forums) helps everyone.
  If your antivirus flags it, you can report a false positive to Microsoft at https://www.microsoft.com/wdsi/filesubmission

Privacy
  Nothing is sent anywhere unless you ask for it. The update check contacts GitHub only if you agreed to it at the
  first start (Settings -> Check for Updates at Start) or when you pick Settings -> Check for Updates; nothing about
  you, your game or your mods is sent. Your mods and settings stay in the program's "data" folder.
  Nexus Mods is contacted only when you use its features (Find My Mods, Check for Updates, or checking at start if you
  turn that on). It only reads public information about Marvel Heroes Omega mods: no Nexus account or key is used, and
  nothing about you or your mods is sent.

Source code and license (MIT): https://github.com/leeper48/MHO-UPK-Tools
Thanks to the author of MHModManager, whose tool and mod format this builds on.
