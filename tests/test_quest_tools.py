"""Quest helpers: project settings parsing, adb discovery, LAN addresses (no headset needed)."""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("quest", ROOT / "tools" / "quest" / "quest.py")
quest = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(quest)

SETTINGS = """PlayerSettings:
  productName: OnDemandMeshVR
  applicationIdentifier:
    Android: com.example.ondemand
    Standalone: com.example.desktop
  buildNumber:
    Standalone: 0
  AndroidMinSdkVersion: 32
  AndroidTargetSdkVersion: 34
"""


def test_project_android_settings():
    assert quest.project_android_settings(SETTINGS) == ("com.example.ondemand", 32)
    assert quest.project_android_settings(SETTINGS.replace("\n", "\r\n")) == ("com.example.ondemand", 32)
    assert quest.project_android_settings("nothing here") == (None, None)


def test_real_project_has_a_package_name():
    package, min_sdk = quest.project_android_settings()
    assert package and "." in package and min_sdk


def test_app_files_dir_is_persistent_data_path():
    assert quest.app_files_dir("com.a.b") == "/sdcard/Android/data/com.a.b/files"


def test_override_file_name_matches_unity():
    cs = (ROOT / "unity" / "OnDemandMeshVR" / "Assets" / "Scripts" / "EdgeClient.cs").read_text(encoding="utf-8")
    assert f'ServerOverrideFile = "{quest.OVERRIDE_FILE}"' in cs


def test_find_adb_returns_an_existing_file_or_none():
    path = quest.find_adb()
    assert path is None or Path(path).exists()


def test_lan_addresses_exclude_loopback():
    from edge.netinfo import lan_ipv4_addresses

    for ip in lan_ipv4_addresses():
        assert not ip.startswith(("127.", "169.254.")) and ip.count(".") == 3


def test_apk_path_matches_the_unity_build_script():
    cs = (ROOT / "unity" / "OnDemandMeshVR" / "Assets" / "Editor" / "QuestBuild.cs").read_text(encoding="utf-8")
    assert f'OutputFile = "{quest.APK.name}"' in cs
    assert quest.APK.parent.name == "Builds"  # Builds/ is git-ignored by unity/.gitignore


def test_quest_build_script_lowers_the_minimum_api_level_to_29():
    cs = (ROOT / "unity" / "OnDemandMeshVR" / "Assets" / "Editor" / "QuestBuild.cs").read_text(encoding="utf-8")
    assert "AndroidApiLevel29" in cs
