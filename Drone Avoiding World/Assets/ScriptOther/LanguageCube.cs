using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// A language cube at the spawn area. Interact with it and every Text Parent (an object with a LanguageText on it)
/// switches to this cube's language. Local only (sync mode: None): each player chooses for themselves.
///
/// SETUP (per cube)
///  - A Collider on the cube, so it can be interacted with.
///  - textGroups   : drag ALL the Text Parents in here. Fill it on ONE cube, then duplicate that cube (Ctrl+D)
///                   for the other languages: the list comes along.
///                   (Alternative: put all Text Parents under ONE empty parent at position 0,0,0 and assign that to
///                   textsRoot instead. Then new texts only need to be dropped under that parent.)
///  - languageIndex: position of this language in the Text Parents' 'languageObjects' list (0 = first, 1 = second, ...).
///  - languageCode : VRChat language code of this cube ("en", "es", "zh-CN", ...). Optional.
///                   At world start, the cube whose code matches the player's VRChat language applies itself,
///                   so most players see their own language without pressing anything.
///                   If no cube matches, the Text Parents keep their 'defaultLanguage'.
///  - Optional: set the text shown when pointing at the cube with "Interaction Text" on the UdonBehaviour.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class LanguageCube : UdonSharpBehaviour
{
    [Tooltip("Drag ALL the Text Parents here (the objects that have a LanguageText). Duplicate this cube for the other languages and the list comes along.")]
    public LanguageText[] textGroups;

    [Tooltip("Optional alternative to the list: ONE parent that contains all the Text Parents (any depth). Only used when the list above is empty.")]
    public Transform textsRoot;

    [Tooltip("Which language this cube selects: position in each Text Parent's list. 0 = first, 1 = second, ...")]
    public int languageIndex = 0;

    [Tooltip("VRChat language code of this cube, for example en, es, zh-CN. Leave empty to skip the automatic choice.")]
    public string languageCode = "";

    private LanguageText[] groups;

    void Start()
    {
        FindGroups();

        // Wait a few frames so every Text Parent has run its own Start() (it applies its default language there).
        if (languageCode != "") SendCustomEventDelayedFrames(nameof(AutoSelect), 3);
    }

    public override void Interact()
    {
        Apply();
    }

    // Runs once at world start on every cube that has a language code.
    public void AutoSelect()
    {
        if (VRCPlayerApi.GetCurrentLanguage() == languageCode) Apply();
    }

    private void FindGroups()
    {
        if (textGroups != null && textGroups.Length > 0)
        {
            groups = textGroups;
            return;
        }

        if (textsRoot != null)
        {
            groups = textsRoot.GetComponentsInChildren<LanguageText>(true);
            return;
        }

        Debug.LogWarning("[LanguageCube] " + gameObject.name + ": no Text Groups assigned");
    }

    private void Apply()
    {
        if (groups == null) FindGroups();
        if (groups == null) return;

        for (int i = 0; i < groups.Length; i++)
        {
            if (groups[i] != null) groups[i].ApplyLanguage(languageIndex);
        }
    }
}
