#!/usr/bin/env python3
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CONFIG = ROOT / "config.json"

APP_ID = "312520"
SBCS_ID = "2928752589"
NS = "CameraScrollFix"

REF_NAMES = [
    "BepInEx.dll",
    "HOOKS-Assembly-CSharp.dll",
    "PUBLIC-Assembly-CSharp.dll",
    "MonoMod.RuntimeDetour.dll",
    "MonoMod.Utils.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.ImageConversionModule.dll",
]


def steam_roots():
    candidates = []
    if sys.platform == "win32":
        try:
            import winreg
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
            val, _ = winreg.QueryValueEx(key, "SteamPath")
            candidates.append(Path(val))
        except Exception:
            pass
        candidates += [
            Path(r"C:\Program Files (x86)\Steam"),
            Path(r"C:\Program Files\Steam"),
        ]
    else:
        home = Path.home()
        candidates += [
            home / ".steam" / "steam",
            home / ".local" / "share" / "Steam",
            home / ".var" / "app" / "com.valvesoftware.Steam" / "data" / "Steam",
        ]
    return [c for c in candidates if c.exists()]


def library_folders(steam):
    libs = [steam]
    vdf = steam / "steamapps" / "libraryfolders.vdf"
    if vdf.exists():
        text = vdf.read_text(encoding="utf-8", errors="ignore")
        for m in re.finditer(r'"path"\s*"([^"]+)"', text):
            libs.append(Path(m.group(1).replace("\\\\", "\\")))
    return libs


def find_game(cfg):
    if cfg.get("game"):
        return Path(cfg["game"])
    for steam in steam_roots():
        for lib in library_folders(steam):
            game = lib / "steamapps" / "common" / "Rain World"
            if (game / "RainWorld_Data").exists():
                return game
    return None


def find_workshop(cfg):
    if cfg.get("workshop"):
        return Path(cfg["workshop"])
    for steam in steam_roots():
        for lib in library_folders(steam):
            ws = lib / "steamapps" / "workshop" / "content" / APP_ID
            if ws.exists():
                return ws
    return None


def find_file(name, roots):
    for r in roots:
        if r and r.exists():
            for p in r.rglob(name):
                return p
    return None


def main():
    cfg = {}
    if CONFIG.exists():
        cfg = json.loads(CONFIG.read_text(encoding="utf-8"))

    game = find_game(cfg)
    if not game or not game.exists():
        sys.exit('Rain World not found. Set "game" in config.json.')

    workshop = find_workshop(cfg)
    if not workshop or not workshop.exists():
        sys.exit('Workshop folder not found. Subscribe to SBCameraScroll or set "workshop" in config.json.')

    print("game     :", game)
    print("workshop :", workshop)

    ref_dir = ROOT / "references"
    ref_dir.mkdir(exist_ok=True)

    managed = game / "RainWorld_Data" / "Managed"
    bepinex = game / "BepInEx"

    for name in REF_NAMES:
        p = find_file(name, [managed, bepinex])
        if not p:
            sys.exit("missing reference: " + name + " (is BepInEx installed?)")
        shutil.copy2(p, ref_dir / name)
        print("ref  ", name)

    sbcs = find_file("SBCameraScroll.dll", [workshop / SBCS_ID, workshop])
    if not sbcs:
        sys.exit("SBCameraScroll.dll not found. Subscribe to the mod on Steam.")
    shutil.copy2(sbcs, ref_dir / "SBCameraScroll.dll")
    print("ref   SBCameraScroll.dll")

    proj = ROOT / "src" / (NS + ".csproj")
    try:
        subprocess.run(["dotnet", "build", str(proj), "-c", "Release"], check=True)
    except FileNotFoundError:
        sys.exit("dotnet not found. Install the .NET SDK.")
    except subprocess.CalledProcessError:
        sys.exit("build failed.")

    dll = ROOT / NS / "plugins" / (NS + ".dll")
    if dll.exists():
        print("OK", dll)
    else:
        print("build finished but DLL not found in plugins/")


if __name__ == "__main__":
    main()
