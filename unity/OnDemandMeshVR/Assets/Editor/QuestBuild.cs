using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Menu Tools > Quest: sets the project up for the Meta Quest and builds the .apk, so the settings that matter
/// are applied by code instead of by hand. Safe to run many times: it only changes what is not already right.
///
///   Prepare project                          only the settings, nothing is built
///   Prepare and build APK                    builds Builds/OnDemandMeshVR.apk (install it with adb or Build And Run)
///   Prepare, build and run on headset        also installs and starts it on the connected Quest
/// </summary>
public static class QuestBuild
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string SimulatorName = "XR Device Simulator";
    const string OutputFile = "OnDemandMeshVR.apk";
    const AndroidSdkVersions MinSdk = AndroidSdkVersions.AndroidApiLevel29;  // Meta's recommended minimum for Quest

    [MenuItem("Tools/Quest/Prepare project")]
    static void PrepareMenu() => Run(build: false, run: false);

    [MenuItem("Tools/Quest/Prepare and build APK")]
    static void BuildMenu() => Run(build: true, run: false);

    [MenuItem("Tools/Quest/Prepare, build and run on headset")]
    static void BuildAndRunMenu() => Run(build: true, run: true);

    static void Run(bool build, bool run)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Info("Esci dalla modalità Play (premi il pulsante Play in alto) e riprova.");
            return;
        }
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
        {
            Info("Manca il supporto Android di Unity.\n\nUnity Hub → Installs → (la tua versione) → Add modules → " +
                 "Android Build Support, con OpenJDK e Android SDK & NDK Tools.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            if (!EditorUtility.DisplayDialog("Passare ad Android",
                    "La piattaforma attiva non è Android. Unity deve cambiarla e riconvertire le risorse: " +
                    "può richiedere diversi minuti.\n\nQuando ha finito (la barra in basso si ferma), " +
                    "ripeti la stessa voce di menu.", "Passa ad Android", "Annulla"))
                return;
            // the switch ends with a script reload, which would kill this method: it cannot continue by itself
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            return;
        }

        var changes = Prepare();
        foreach (var change in changes)
            Debug.Log("[QuestBuild] " + change);
        if (!build)
        {
            Info(changes.Count == 0
                ? "Il progetto era già pronto: nessuna modifica."
                : "Modifiche fatte:\n\n- " + string.Join("\n- ", changes));
            return;
        }
        Build(run, changes);
    }

    /// <summary>Applies the Quest settings; returns a description of what it changed.</summary>
    static List<string> Prepare()
    {
        var changes = new List<string>();

        if ((int)PlayerSettings.Android.minSdkVersion > (int)MinSdk)
        {
            // The VR template asks for Android 14 (API 34): a Quest 2 running an older system refuses to install it.
            changes.Add($"Minimum API Level: {PlayerSettings.Android.minSdkVersion} -> {MinSdk}");
            PlayerSettings.Android.minSdkVersion = MinSdk;
        }
        if (!PlayerSettings.Android.forceInternetPermission)
        {
            changes.Add("Internet Access: Auto -> Require (the app talks to the edge server)");
            PlayerSettings.Android.forceInternetPermission = true;
        }
        if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
        {
            changes.Add("Allow downloads over HTTP: Always allowed (the edge server is plain http on the local network)");
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        }
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP)
        {
            changes.Add("Scripting Backend: IL2CPP (required for ARM64)");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        }
        if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
        {
            changes.Add("Target Architectures: ARM64 only (the Quest CPU)");
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        }
        if (EditorUserBuildSettings.buildAppBundle)
        {
            changes.Add("Build App Bundle (Google Play): off, the Quest needs an .apk");
            EditorUserBuildSettings.buildAppBundle = false;
        }

        var tagged = TagSimulatorEditorOnly();
        if (tagged != null)
            changes.Add(tagged);
        return changes;
    }

    /// <summary>The XR Device Simulator is for the Editor: tagged EditorOnly it is left out of the build.</summary>
    static string TagSimulatorEditorOnly()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var simulator = scene.GetRootGameObjects().Select(r => FindByName(r.transform, SimulatorName)).FirstOrDefault(t => t != null);
        if (simulator == null)
            return null;  // already removed from the scene: nothing to tag
        if (simulator.gameObject.CompareTag("EditorOnly"))
            return null;
        simulator.gameObject.tag = "EditorOnly";
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"'{SimulatorName}' tagged EditorOnly (not included in the build) and scene saved";
    }

    static Transform FindByName(Transform root, string name)
    {
        if (root.name == name)
            return root;
        foreach (Transform child in root)
        {
            var found = FindByName(child, name);
            if (found != null)
                return found;
        }
        return null;
    }

    static void Build(bool run, List<string> changes)
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var outputDir = Path.Combine(projectRoot, "Builds");
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, OutputFile);

        Debug.Log("[QuestBuild] building " + output + (run ? " and running it on the headset" : "") +
                  ". The first build is slow (IL2CPP + shaders): 20-30 minutes on a weak PC.");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = run ? BuildOptions.AutoRunPlayer : BuildOptions.None
        });

        var summary = report.summary;
        if (summary.result == BuildResult.Succeeded)
        {
            // summary.totalSize is the whole output folder (debug symbols included, > 1 GB): the app is the .apk
            var mb = File.Exists(output) ? new FileInfo(output).Length / (1024.0 * 1024.0) : summary.totalSize / (1024.0 * 1024.0);
            Debug.Log($"[QuestBuild] build OK: {output} ({mb:F0} MB) in {summary.totalTime.TotalMinutes:F1} min");
            if (EditorUtility.DisplayDialog("Build completata",
                    $"{output}\nL'app pesa {mb:F0} MB. Build: {summary.totalTime.TotalMinutes:F1} minuti." +
                    (run
                        ? "\n\nUnity ha provato a installarla sul visore. Se il visore non era collegato e acceso, " +
                          "non è stata installata: usa tools\\quest\\install_app.bat (controlla anche la Console)."
                        : "\n\nOra puoi installarla sul visore: tools\\quest\\install_app.bat (guida, parte 5)."),
                    "Apri la cartella", "Chiudi"))
                EditorUtility.RevealInFinder(output);
            return;
        }

        var errors = report.steps.SelectMany(s => s.messages).Where(m => m.type == LogType.Error)
                           .Select(m => m.content).Distinct().Take(5).ToList();
        foreach (var e in errors)
            Debug.LogError("[QuestBuild] " + e);
        Info($"Build non riuscita: {summary.result}.\n\n" +
             (errors.Count > 0 ? "Primi errori:\n- " + string.Join("\n- ", errors.Select(Shorten)) : "Guarda la Console.") +
             "\n\nI messaggi completi sono nella Console di Unity.");
    }

    static string Shorten(string s) => s.Length <= 220 ? s : s.Substring(0, 220) + "...";

    static void Info(string message) => EditorUtility.DisplayDialog("Quest", message, "OK");
}
