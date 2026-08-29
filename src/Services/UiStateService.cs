namespace ASAP.Services;

/// <summary>
/// 회로(circuit) 단위 UI 상태 — AppBar(MainLayout)와 Workbench가 공유.
/// 현재 DDS 작업 공간과 다크모드 선호를 들고 변경을 통지한다.
/// </summary>
public class UiStateService
{
    public bool IsDarkMode { get; private set; } = true; // dark-first
    public string WorkspaceLabel { get; private set; } = "새 DDS 세션";
    public string? WorkspaceDetail { get; private set; } = "DDS 작업 공간";

    public event Action? Changed;

    public void SetDarkMode(bool dark)
    {
        if (IsDarkMode == dark) return;
        IsDarkMode = dark;
        Changed?.Invoke();
    }

    public void SetWorkspace(string label, string? detail = null)
    {
        WorkspaceLabel = string.IsNullOrWhiteSpace(label) ? "스튜디오" : label;
        WorkspaceDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        Changed?.Invoke();
    }
}
