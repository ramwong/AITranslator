namespace AITranslator.Models;

/// <summary>應用程式設定，屬性初始化器即預設值。</summary>
public class AppSettings
{
    public int PopupWidth { get; set; } = 420;

    public int PopupHeight { get; set; } = 260;

    public float FontSize { get; set; } = 12f;

    public string PromptTemplate { get; set; } =
        "請將以下內容翻譯成繁體中文（台灣用語），只輸出譯文，不要解釋或加註:\n{text}";

    public string Model { get; set; } = "beam:cloud";

    public string OllamaUrl { get; set; } = "http://localhost:11434";

    public string Hotkey { get; set; } = "Ctrl+Alt+E";

    /// <summary>朗讀選取文字（TTS）的快捷鍵，不可與 <see cref="Hotkey"/> 相同。</summary>
    public string TtsHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>辨識（OCR）使用的模型名稱。</summary>
    public string OcrModel { get; set; } = "gemma4:31b-cloud";

    /// <summary>擷取指定畫面區域進行 OCR 的快捷鍵，不可與 <see cref="Hotkey"/>、<see cref="TtsHotkey"/> 相同。</summary>
    public string OcrHotkey { get; set; } = "Ctrl+Alt+I";

    /// <summary>OCR 直接以螢幕上可見的原文辨識並翻譯；停用時改以文字選取方式取得原文。</summary>
    public bool OcrTranslateInVision { get; set; }

    public int MaxTextLength { get; set; } = 5000;

    public bool StartWithWindows { get; set; }
}