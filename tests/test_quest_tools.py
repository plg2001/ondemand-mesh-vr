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


class _FakeAdb:
    def __init__(self, outputs):
        self.outputs = list(outputs)

    def shell(self, *args):
        return self.outputs.pop(0)


def test_clock_offset_uses_headset_date(monkeypatch):
    clock = iter([100.0, 100.2, 200.0, 200.1])  # PC time before/after each adb call
    monkeypatch.setattr(quest.time, "time", lambda: next(clock))
    off = quest.clock_offset(_FakeAdb(["114.2", "214.15"]), samples=2)
    assert off == 14.1  # second sample: shortest round trip, midpoint 200.05


def test_clock_offset_without_a_number_is_none():
    assert quest.clock_offset(_FakeAdb(["date: bad format"] * 3), samples=3) is None


def test_copy_server_rows_keeps_the_session_window(tmp_path):
    src, dest = tmp_path / "log.csv", tmp_path / "out.csv"
    src.write_text("timestamp,object_id\n2026-11-11T11:00:00.000,a\n2026-11-11T11:05:00.000,b\n"
                   "2026-11-11T11:20:00.000,c\n", encoding="utf-8")
    start = quest.dt.datetime(2026, 11, 11, 11, 1).timestamp()
    end = quest.dt.datetime(2026, 11, 11, 11, 10).timestamp()
    assert quest.copy_server_rows(src, dest, start, end) == 1
    assert dest.read_text(encoding="utf-8").splitlines()[1].endswith(",b")


def test_session_bat_files_exist():
    for link in ("usb", "wifi"):
        bat = (ROOT / "tools" / "quest" / f"session_{link}.bat").read_text(encoding="utf-8")
        assert f"quest.py session {link}" in bat


def test_adb_candidates_for_each_system(tmp_path):
    hub = tmp_path / "Unity" / "Hub" / "Editor" / "6000.3.26f1" / "Editor" / "Data" / "PlaybackEngines" / \
        "AndroidPlayer" / "SDK" / "platform-tools"
    hub.mkdir(parents=True)
    (hub / "adb").write_text("")
    linux = quest.adb_candidates("linux", home=tmp_path, env={"ANDROID_HOME": "/opt/sdk"})
    assert linux[0] == Path("/opt/sdk/platform-tools/adb")           # an SDK named in the environment first
    assert linux[1] == hub / "adb"                                    # then Unity's own adb
    assert linux[2] == tmp_path / "Android" / "Sdk" / "platform-tools" / "adb"
    windows = quest.adb_candidates("win32", home=tmp_path, env={"LOCALAPPDATA": str(tmp_path / "Local")})
    assert all(p.name == "adb.exe" for p in windows)
    assert tmp_path / "Local" / "Android" / "Sdk" / "platform-tools" / "adb.exe" in windows
    mac = quest.adb_candidates("darwin", home=tmp_path, env={})
    assert mac[0] == tmp_path / "Library" / "Android" / "sdk" / "platform-tools" / "adb"


def test_headset_utc_offset():
    assert quest.headset_utc_offset(_FakeAdb(["+0100"])) == 3600
    assert quest.headset_utc_offset(_FakeAdb(["-0530"])) == -(5 * 3600 + 30 * 60)
    assert quest.headset_utc_offset(_FakeAdb(["date: unknown option"])) is None


def test_server_clock_offset_without_a_server_is_none():
    assert quest.server_clock_offset("http://127.0.0.1:9", samples=1) is None
