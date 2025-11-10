#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// 각 컴포넌트 섹션(InspectorElement) 하단에 "빠른 적용/설정/프리셋 저장" 툴바를 삽입해주는 확장.
/// - 선택/계층/프로젝트 변경 시에만 재주입(디바운스)
/// - 섹션당 1회만 주입(마커 클래스)
/// - 프리셋 수집/적용/편집 공통 유틸 분리
/// </summary>
[InitializeOnLoad]
public static class PresetExtension
{
    // ===== UI/상태 상수 =====
    private const string kInjectedMarkerClass = "nb-per-component-injected";
    private const string kCatcherClass = "nb-object-picker-catcher";
    private const string kPrefShowKey = "Editor_Preset_Show";
    private const string kMenuPath = "Noisy Bird/UI Extension/[On,Off] Quickly Preset";

    // 디바운스용
    private static double _nextInjectAt;
    private const double kDebounceSec = 0.05;

    // 루트창 캐치 유무 체크용 (중복 콜백 등록 방지)
    private static readonly HashSet<int> _hookedWindows = new HashSet<int>();

    private static bool IsShow
    {
        get => EditorPrefs.GetBool(kPrefShowKey, true);
        set => EditorPrefs.SetBool(kPrefShowKey, value);
    }

    static PresetExtension()
    {
        // 초기 훅
        EditorApplication.delayCall += EnsureHookAllInspectors;

        // 에디터 변화에 반응: 선택/계층/프로젝트
        Selection.selectionChanged += RequestReinject;
        EditorApplication.hierarchyChanged += RequestReinject;
        EditorApplication.projectChanged += RequestReinject;

        // 디바운스 타이머
        EditorApplication.update += OnUpdate;
    }

    // ===== 메뉴 토글 =====
    [MenuItem(kMenuPath)]
    private static void ToggleShowPresetExtension()
    {
        IsShow = !IsShow;
        Menu.SetChecked(kMenuPath, IsShow);
        RequestReinject();
    }

    [MenuItem(kMenuPath, true)]
    private static bool ToggleShowPresetExtension_Validate()
    {
        Menu.SetChecked(kMenuPath, IsShow);
        return true;
    }

    // ===== 주입 스케줄링 =====
    private static void RequestReinject()
    {
        if (!IsShow) return;
        _nextInjectAt = EditorApplication.timeSinceStartup + kDebounceSec;
    }

    private static void OnUpdate()
    {
        if (!IsShow) return;
        if (_nextInjectAt > 0 && EditorApplication.timeSinceStartup >= _nextInjectAt)
        {
            _nextInjectAt = 0;
            EnsureHookAllInspectors();
        }
    }

    // ===== 인스펙터 훅/주입 =====
    private static void EnsureHookAllInspectors()
    {
        if (!IsShow) return;

        var inspectorType = Type.GetType("UnityEditor.InspectorWindow, UnityEditor");
        if (inspectorType == null) return;

        var windows = Resources.FindObjectsOfTypeAll(inspectorType);
        foreach (var w in windows)
            TryHookWindow(w as EditorWindow);
    }

    private static void TryHookWindow(EditorWindow win)
    {
        if (win == null) return;

        int id = win.GetInstanceID();

        var root = win.rootVisualElement;
        if (root == null) return;
        
        if (_hookedWindows.Add(id))
        {
            // 창이 닫히거나 패널에서 분리되면 호출
            root.RegisterCallback<DetachFromPanelEvent>(_ => _hookedWindows.Remove(id));
        }

        // 중복 캐처 삽입 방지
        if (root.Q<IMGUIContainer>(className: kCatcherClass) == null)
        {
            var catcher = new IMGUIContainer { pickingMode = PickingMode.Ignore };
            catcher.AddToClassList(kCatcherClass);
            root.Add(catcher);
        }

        // 레이아웃 변경 → 주입 (여기서는 재주입 요청만, 실제는 디바운스)
        root.RegisterCallback<GeometryChangedEvent>(_ => RequestReinject());

        // 최초 1회 즉시 시도
        InjectPerSectionToolbar(root);
    }

    private static void InjectPerSectionToolbar(VisualElement root)
    {
        if (!IsShow || root == null) return;

        // 현재 활성 에디터들
        var editors = ActiveEditorTracker.sharedTracker?.activeEditors;
        if (editors == null || editors.Length == 0) return;

        // 각 컴포넌트 섹션(InspectorElement)
        var sections = root.Query<VisualElement>(className: "unity-inspector-element").ToList();
        if (sections == null || sections.Count == 0) return;

        // 섹션 수와 에디터 수 매칭
        int count = Math.Min(sections.Count, editors.Length);
        for (int i = 0; i < count; i++)
        {
            var section = sections[i];
            var editor = editors[i];
            if (section == null || editor == null) continue;

            // GameObject/Transform 등 제외하려면 필터
            if (editor.target is GameObject) continue;

            // 이미 주입된 섹션은 스킵
            if (section.ClassListContains(kInjectedMarkerClass)) continue;

            // 타이틀(0) 직후에 툴바 삽입
            var toolbar = BuildToolbarFor(editor);
            var insertIndex = Math.Min(1, section.childCount);
            section.Insert(insertIndex, toolbar);

            section.AddToClassList(kInjectedMarkerClass);
        }
    }

    private static VisualElement BuildToolbarFor(Editor editor)
    {
        var bar = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                justifyContent = Justify.FlexEnd,
                alignItems = Align.Center,
                marginTop = 2,
                marginBottom = 2
            }
        };

        var quick = new Button(() => ShowPresetApplyMenu(editor.targets)) { text = "빠른 적용 ▼" };
        quick.style.height = 20; quick.style.marginRight = 4;

        var settings = new Button(() => ShowPresetOpenMenu(editor.targets)) { text = "설정 ▼" };
        settings.style.height = 20; settings.style.marginRight = 4;

        var save = new Button(() =>
        {
            var target = editor.targets != null && editor.targets.Length > 0 ? editor.targets[0] : null;
            if (target != null)
            {
                var typeName = target.GetType().Name;
                SavePresetWithDialog(target, $"Presets/{typeName}");
            }
        })
        { text = "프리셋 저장" };
        save.style.height = 20;

        bar.Add(quick);
        bar.Add(settings);
        bar.Add(save);
        return bar;
    }

    // ===== 프리셋 메뉴 =====
    private static void ShowPresetApplyMenu(Object[] targets)
    {
        var menu = new GenericMenu();
        var sample = targets?.FirstOrDefault();

        if (!sample)
        {
            menu.AddDisabledItem(new GUIContent("대상이 없음"));
            menu.ShowAsContext();
            return;
        }

        var presets = EnumerateCompatiblePresets(sample).ToList();
        if (presets.Count == 0)
        {
            menu.AddDisabledItem(new GUIContent("호환 프리셋 없음"));
        }
        else
        {
            foreach (var (preset, displayName) in presets)
            {
                menu.AddItem(new GUIContent(displayName), false, () => ApplyPresetToTargets(preset, targets));
            }
        }

        menu.AddSeparator("");
        menu.AddItem(new GUIContent("Preset 설정"), false, () => PresetSelector.ShowSelector(targets, null, true));
        menu.AddItem(new GUIContent("Preset Manager (Project Settings)"), false,
            () => SettingsService.OpenProjectSettings("Project/Preset Manager"));

        menu.ShowAsContext();
    }

    private static void ShowPresetOpenMenu(Object[] targets)
    {
        var menu = new GenericMenu();
        var sample = targets?.FirstOrDefault();

        if (!sample)
        {
            menu.AddDisabledItem(new GUIContent("대상이 없음"));
            menu.ShowAsContext();
            return;
        }

        var presets = EnumerateCompatiblePresets(sample).ToList();
        if (presets.Count == 0)
        {
            menu.AddDisabledItem(new GUIContent("호환 프리셋 없음"));
        }
        else
        {
            foreach (var (preset, displayName) in presets)
            {
                menu.AddItem(new GUIContent($"[설정] {displayName}"), false, () => OpenLockedInspector(preset));
            }
        }

        menu.AddSeparator("");
        menu.AddItem(new GUIContent("Preset 설정"), false, () => PresetSelector.ShowSelector(targets, null, true));
        menu.AddItem(new GUIContent("Preset Manager (Project Settings)"), false,
            () => SettingsService.OpenProjectSettings("Project/Preset Manager"));

        menu.ShowAsContext();
    }

    private static IEnumerable<(Preset preset, string displayName)> EnumerateCompatiblePresets(Object sample)
    {
        // t:Preset 전체를 탐색하되 예외 안전하게 로드
        foreach (var guid in AssetDatabase.FindAssets("t:Preset"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            Preset p = null;
            try { p = AssetDatabase.LoadAssetAtPath<Preset>(path); }
            catch { /* 무시 */ }

            if (!p) continue;
            if (!p.CanBeAppliedTo(sample)) continue;

            var name = string.IsNullOrEmpty(p.name)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : p.name;

            yield return (p, name);
        }
    }

    private static void ApplyPresetToTargets(Preset preset, Object[] targets)
    {
        if (!preset || targets == null || targets.Length == 0) return;

        Undo.IncrementCurrentGroup();
        var group = Undo.GetCurrentGroup();

        foreach (var t in targets)
        {
            if (!t) continue;
            if (!preset.CanBeAppliedTo(t)) continue;

            Undo.RecordObject(t, "Apply Preset");
            preset.ApplyTo(t);

            if (t is AssetImporter ai)
                ai.SaveAndReimport();

            EditorUtility.SetDirty(t);
        }

        Undo.CollapseUndoOperations(group);
    }

    // ===== 프리셋 저장 유틸 =====
    /// <summary>
    /// 주어진 객체의 현재 설정값으로 Preset을 만들어 .preset 에셋으로 저장(대화상자).
    /// </summary>
    public static Preset SavePresetWithDialog(Object source, string defaultFolder = "Presets")
    {
        if (!source)
        {
            EditorUtility.DisplayDialog("프리셋 저장", "대상이 없습니다.", "확인");
            return null;
        }

        // 폴더 보장
        EnsureFolderExists($"Assets/{defaultFolder}");

        var typeName = source.GetType().Name;
        var defaultName = GetUniquePresetFileName(typeName, defaultFolder);

        var path = EditorUtility.SaveFilePanelInProject(
            "프리셋 저장",
            defaultName,
            "preset",
            "프리셋을 저장할 위치와 파일명을 선택하세요.",
            $"Assets/{defaultFolder}"
        );

        if (string.IsNullOrEmpty(path)) return null; // 취소

        return SavePresetToPath(source, path);
    }

    private static string GetUniquePresetFileName(string typeName, string defaultFolder)
    {
        var i = 0;
        while (true)
        {
            var name = i == 0 ? $"{typeName}.preset" : $"{typeName}{i}.preset";
            var abs = $"{Application.dataPath}/{defaultFolder}/{name}";
            if (!File.Exists(abs)) return name;
            i++;
        }
    }

    /// <summary>
    /// 지정 경로에 Preset 저장(기존 있으면 교체). 저장/리프레시 및 Ping.
    /// </summary>
    public static Preset SavePresetToPath(Object source, string assetPath)
    {
        if (!source || string.IsNullOrEmpty(assetPath)) return null;

        var preset = new Preset(source);

        var existing = AssetDatabase.LoadAssetAtPath<Preset>(assetPath);
        if (existing != null)
            AssetDatabase.DeleteAsset(assetPath);

        AssetDatabase.CreateAsset(preset, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorGUIUtility.PingObject(preset);
        Debug.Log($"[PresetExtension] Saved: {assetPath}");
        return preset;
    }

    private static void EnsureFolderExists(string assetFolder)
    {
        if (AssetDatabase.IsValidFolder(assetFolder)) return;

        var abs = Path.Combine(Directory.GetCurrentDirectory(), assetFolder.Replace("Assets/", "Assets\\"));
        Directory.CreateDirectory(abs);
        AssetDatabase.Refresh();
    }

    // EditorUtility.OpenPropertyEditor는 해당 인스턴스를 잠그고 보여주는 별도 인스펙터
    private static void OpenLockedInspector(Preset preset)
    {
        if (!preset) return;
        EditorUtility.OpenPropertyEditor(preset);
    }
}
#endif
