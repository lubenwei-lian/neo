#include <windows.h>

static void Fail(const wchar_t* msg)
{
    MessageBoxW(NULL, msg, L"neo", MB_ICONERROR | MB_OK);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, LPWSTR lpCmdLine, int nShow)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
    {
        Fail(L"无法定位程序目录。");
        return 1;
    }
    wchar_t* slash = wcsrchr(dir, L'\\');
    if (slash == NULL)
    {
        Fail(L"无法定位程序目录。");
        return 1;
    }
    *slash = L'\0';

    SetEnvironmentVariableW(L"NEO_HOME", dir);

    wchar_t exe[MAX_PATH + 16];
    wcscpy(exe, dir);
    wcscat(exe, L"\\app\\neo.exe");
    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES)
    {
        Fail(L"找不到程序文件 app\\neo.exe。\n请重新安装 neo。");
        return 1;
    }

    const wchar_t* full = GetCommandLineW();
    const wchar_t* p = full;
    if (*p == L'"')
    {
        p++;
        while (*p && *p != L'"') p++;
        if (*p == L'"') p++;
    }
    else
    {
        while (*p && *p != L' ' && *p != L'\t') p++;
    }
    while (*p == L' ' || *p == L'\t') p++;

    wchar_t cmd[32768];
    if (wcslen(exe) + wcslen(p) + 8 >= 32768)
    {
        Fail(L"路径过长。");
        return 1;
    }
    wcscpy(cmd, L"\"");
    wcscat(cmd, exe);
    wcscat(cmd, L"\" ");
    wcscat(cmd, p);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));

    if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE, 0, NULL, dir, &si, &pi))
    {
        Fail(L"启动程序失败。\n请尝试重新安装 neo。");
        return 1;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
