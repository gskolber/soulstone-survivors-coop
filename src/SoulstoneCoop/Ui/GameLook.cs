using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

/// <summary>
/// The game's own UI pieces for the Co-op panel (docs/ui-nativa-investigacao.md):
/// - sprites by name, as loaded in the menu (Menu_Frame_NotTransparent, Title_Frame, Fight_Button1/Fight_Button,
///   Button/Button_Selected, Close_Button/_HL, Craft_Background_List_Frame, the 128x128 character icons...);
/// - buttons cloned from a main-menu tab (Image + Button with sprite swap + UIButtonSound for the click + the
///   EventTrigger that plays the hover sound + the label with LabelReferences for the per-language font), stripped of
///   its animations and localization and re-skinned;
/// - the text field cloned from the newsletter form (MailingFormPanel.EmailInput).
/// Anything missing falls back to plain uGUI, so a game update degrades the look instead of the panel.
/// </summary>
internal static class GameLook
{
    internal enum Style { Primary, Secondary, Close }

    private static Dictionary<string, Sprite>? _sprites;
    private static GameObject? _buttonTemplate;
    private static TMP_InputField? _inputTemplate;

    internal static TMP_FontAsset? Font;

    /// <summary>The menu scene was (re)loaded: drop the cached references to its objects.</summary>
    internal static void Reset()
    {
        _sprites = null;
        _buttonTemplate = null;
        _inputTemplate = null;
    }

    internal static void SetTemplates(GameObject? tab, TMP_InputField? input)
    {
        if (tab != null) _buttonTemplate = tab;
        if (input != null) _inputTemplate = input;
    }

    // ---------------- sprites ----------------

    internal static Sprite? Sprite(string name)
    {
        if (_sprites == null) LoadSprites();
        return _sprites!.TryGetValue(name, out var s) && s != null ? s : null;
    }

    private static void LoadSprites()
    {
        _sprites = new Dictionary<string, Sprite>();
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
            {
                var s = o.TryCast<Sprite>();
                if (s == null) continue;
                string n = s.name;
                // Several sprites share a name (the character icons come in 128 px and bigger): keep the 128 px one,
                // else the first.
                if (_sprites.TryGetValue(n, out var have) && have != null)
                {
                    bool haveIcon = Mathf.Approximately(have.rect.width, 128f) && Mathf.Approximately(have.rect.height, 128f);
                    bool isIcon = Mathf.Approximately(s.rect.width, 128f) && Mathf.Approximately(s.rect.height, 128f);
                    if (haveIcon || !isIcon) continue;
                }
                _sprites[n] = s;
            }
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Loading the game's sprites: {ex.Message}"); }
    }

    /// <summary>The character's 128 px menu icon (sprites are named after CharactersType).</summary>
    internal static Sprite? CharacterIcon(string characterName) =>
        string.IsNullOrEmpty(characterName) ? null : Sprite(characterName);

    /// <summary>The character's name in the game's language (the game's key is the CharactersType name).</summary>
    internal static string CharacterDisplayName(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return "";
        try
        {
            string s = LocalizationManager.GetLocalizedStringOrDefaultToKey(characterName);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { }
        return characterName;
    }

    internal static Image Img(Transform parent, string name, string sprite, Image.Type? type = null, Color? fallback = null)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        var sp = Sprite(sprite);
        if (sp != null)
        {
            img.sprite = sp;
            img.type = type ?? (sp.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple);
        }
        else img.color = fallback ?? new Color(0.06f, 0.05f, 0.08f, 0.94f);
        img.raycastTarget = false;
        return img;
    }

    // ---------------- buttons ----------------

    private static (string Normal, string Hover) Skin(Style s) => s switch
    {
        Style.Primary => ("Fight_Button1", "Fight_Button"),
        Style.Close => ("Close_Button", "Close_Button_HL"),
        _ => ("Button", "Button_Selected"),
    };

    /// <summary>A game button: a clone of a menu tab re-skinned with <paramref name="style"/>'s sprites.</summary>
    internal static Button Button(Transform parent, string name, Style style, string label, Action onClick, float fontSize,
        string sound = "MenuClick")
    {
        Button? b = null;
        if (_buttonTemplate != null)
        {
            try { b = CloneButton(parent, name); }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Cloning a game button: {ex.Message}"); }
        }
        b ??= PlainButton(parent, name);

        var img = b.GetComponent<Image>();
        var (normal, hover) = Skin(style);
        var spN = Sprite(normal);
        var spH = Sprite(hover);
        if (img != null && spN != null)
        {
            img.sprite = spN;
            img.type = Image.Type.Simple;
            img.color = Color.white;
            b.transition = Selectable.Transition.SpriteSwap;
            var ss = b.spriteState;
            ss.highlightedSprite = spH;
            ss.selectedSprite = spH;
            b.spriteState = ss;
        }
        else if (img != null)
        {
            img.sprite = null;
            img.color = new Color(0.30f, 0.12f, 0.12f, 1f);
            b.transition = Selectable.Transition.ColorTint;
        }
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        b.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        b.onClick.AddListener((UnityAction)new Action(() =>
        {
            try { onClick(); }
            catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][ui] {name}: {ex}"); }
        }));
        var snd = b.GetComponent<UIButtonSound>();
        if (snd != null) snd.soundEvent = sound;

        var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (style == Style.Close)
        {
            if (t != null) t.gameObject.SetActive(false);
        }
        else
        {
            t ??= Text(b.transform, "Label", "", fontSize, TextAlignmentOptions.Center);
            t.gameObject.SetActive(true);
            t.text = label;
            t.fontSize = fontSize;
            t.enableAutoSizing = true;
            t.fontSizeMax = fontSize;
            t.fontSizeMin = Mathf.Round(fontSize * 0.55f);
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            // The sprites end in chevrons: keep the text off them.
            var lrt = t.rectTransform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.pivot = new Vector2(0.5f, 0.5f);
            float side = style == Style.Primary ? 0.13f : 0.11f;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
            lrt.anchorMin = new Vector2(side, 0.1f);
            lrt.anchorMax = new Vector2(1f - side, 0.9f);
        }
        return b;
    }

    private static Button CloneButton(Transform parent, string name)
    {
        var go = UnityEngine.Object.Instantiate(_buttonTemplate!, parent, false);
        go.name = name;
        Strip(go);
        // The tab's CanvasGroup is faded by the menu's animation group: gone, the button is always opaque.
        foreach (var g in go.GetComponents<CanvasGroup>()) UnityEngine.Object.DestroyImmediate(g);
        foreach (var le in go.GetComponents<LayoutElement>()) UnityEngine.Object.DestroyImmediate(le);
        foreach (var lg in go.GetComponents<HorizontalLayoutGroup>()) UnityEngine.Object.DestroyImmediate(lg);
        go.SetActive(true);
        var rt = go.GetComponent<RectTransform>();
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return go.GetComponent<Button>();
    }

    private static Button PlainButton(Transform parent, string name)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        try { rt.gameObject.AddComponent<UIButtonSound>(); } catch { }
        Text(rt, "Label", "", 30f, TextAlignmentOptions.Center);
        return b;
    }

    /// <summary>Removes what ties a cloned piece of the game's UI to the game's code: localization (would rewrite
    /// the texts), the animations of the panel it came from, gamepad hint icons and notification badges.</summary>
    internal static void Strip(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<LocalizedMonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(c);
        foreach (var c in go.GetComponentsInChildren<GameSmithing.UIAnimation>(true)) UnityEngine.Object.DestroyImmediate(c);
        foreach (var c in go.GetComponentsInChildren<GameSmithing.UIDeltaTimeAnimationGroup>(true)) UnityEngine.Object.DestroyImmediate(c);
        var drop = new List<GameObject>();
        foreach (var c in go.GetComponentsInChildren<GamepadSpriteAdjuster>(true)) drop.Add(c.gameObject);
        for (int i = 0; i < go.transform.childCount; i++)
        {
            var ch = go.transform.GetChild(i);
            if (ch.name.Contains("Notification") || ch.name.Contains("Controller")) drop.Add(ch.gameObject);
        }
        foreach (var d in drop) if (d != null && d != go) UnityEngine.Object.DestroyImmediate(d);
    }

    // ---------------- text field ----------------

    /// <summary>The newsletter form's e-mail field, cloned and rewired (its events are the form's). Null if the
    /// form isn't there; the caller builds one by hand.</summary>
    internal static TMP_InputField? CloneInput(Transform parent, string name, string placeholder, float fontSize)
    {
        if (_inputTemplate == null) return null;
        try
        {
            var go = UnityEngine.Object.Instantiate(_inputTemplate.gameObject, parent, false);
            go.name = name;
            go.SetActive(false);
            Strip(go);
            foreach (var g in go.GetComponents<CanvasGroup>()) UnityEngine.Object.DestroyImmediate(g);
            var input = go.GetComponent<TMP_InputField>();
            // RemoveAllListeners leaves the persistent ones (MailingFormPanel's): new events instead.
            input.onEndEdit = new TMP_InputField.SubmitEvent();
            input.onSubmit = new TMP_InputField.SubmitEvent();
            input.onValueChanged = new TMP_InputField.OnChangeEvent();
            input.onSelect = new TMP_InputField.SelectionEvent();
            input.onDeselect = new TMP_InputField.SelectionEvent();
            input.contentType = TMP_InputField.ContentType.Standard;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 64;
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            input.pointSize = fontSize;
            foreach (var t in new[] { input.textComponent, input.placeholder?.TryCast<TextMeshProUGUI>() })
            {
                if (t == null) continue;
                t.enableAutoSizing = false;
                t.fontSize = fontSize;
                t.alignment = TextAlignmentOptions.MidlineLeft;
                t.enableWordWrapping = false;
            }
            var ph = input.placeholder?.TryCast<TextMeshProUGUI>();
            if (ph != null) ph.text = placeholder;
            // The focus frame was sized around the form's 366x32 field: around ours instead.
            var hl = go.transform.Find("SelectionHighlight")?.GetComponent<RectTransform>();
            if (hl != null) Stretch(hl, -7f, -7f, -7f, -7f);
            // The text area was laid out for a 32 px line: let it fill the (taller) field.
            if (input.textViewport != null)
            {
                var v = input.textViewport;
                v.anchorMin = Vector2.zero;
                v.anchorMax = Vector2.one;
                v.offsetMin = new Vector2(16f, 4f);
                v.offsetMax = new Vector2(-16f, -4f);
            }
            go.SetActive(true);
            return input;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][ui] Cloning the game's text field: {ex.Message}");
            return null;
        }
    }

    // ---------------- basics ----------------

    internal static RectTransform Rect(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    internal static TextMeshProUGUI Text(Transform parent, string name, string text, float size, TextAlignmentOptions align)
    {
        var rt = Rect(parent, name);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (Font != null) t.font = Font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.enableWordWrapping = false;
        t.richText = true;
        t.raycastTarget = false;
        t.text = text;
        return t;
    }

    internal static void AutoSize(TextMeshProUGUI t, float size, float min = 0.55f)
    {
        t.enableAutoSizing = true;
        t.fontSizeMax = size;
        t.fontSizeMin = Mathf.Round(size * min);
    }

    /// <summary>Child placed from its parent's top-left corner (y grows downward).</summary>
    internal static void TopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    internal static void Stretch(RectTransform rt, float l, float b, float r, float t)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(l, b);
        rt.offsetMax = new Vector2(-r, -t);
    }
}
