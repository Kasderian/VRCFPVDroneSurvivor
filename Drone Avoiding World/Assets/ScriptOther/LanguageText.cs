using UdonSharp;
using UnityEngine;

/// <summary>
/// One translated text. Put it on the parent ("group") of that text's language objects.
/// No sync (sync mode: None): every player picks their own language, nothing is networked.
///
/// SETUP
///  - Under the group, one child per language (a TextMeshPro each, or a sign, an image, anything).
///  - Drag the children into 'languageObjects'. The ORDER must be the same for every group and matches
///    the 'languageIndex' you set on the LanguageCube objects (0 = first language, 1 = second, ...).
///  - Keep the group object itself ACTIVE. Only its children are switched on and off.
///  - A group that has no object for a language (slot empty or list shorter) shows 'defaultLanguage' instead.
///
/// LanguageCube calls ApplyLanguage() on every group under its 'textsRoot'.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class LanguageText : UdonSharpBehaviour
{
    [Tooltip("One object per language, same order in every group. 0 = first language, 1 = second, ...")]
    public GameObject[] languageObjects;

    [Tooltip("Shown at world start, and used when a language has no object in this group. 0 = first language (for example English).")]
    public int defaultLanguage = 0;

    void Start()
    {
        ApplyLanguage(defaultLanguage);
    }

    public void ApplyLanguage(int index)
    {
        if (languageObjects == null || languageObjects.Length == 0) return;

        // this group has nothing for that language: fall back to the default
        if (!HasObject(index)) index = defaultLanguage;
        if (!HasObject(index)) return;   // not even the default exists: leave the group as it is

        for (int i = 0; i < languageObjects.Length; i++)
        {
            if (languageObjects[i] != null) languageObjects[i].SetActive(i == index);
        }
    }

    private bool HasObject(int index)
    {
        return index >= 0 && index < languageObjects.Length && languageObjects[index] != null;
    }
}
