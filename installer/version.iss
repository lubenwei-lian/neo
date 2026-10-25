; ============================================================================
;
; 这里是唯一需要手改版本号的地方之一；neo.iss 用 #include "version.iss" 读取。
; 升版本时必须同步改这几处，tools\publish.ps1 会逐项断言一致，不一致直接 throw：
;   1) 本文件
;   2) src\Neo.App\Neo.App.csproj       <Version> / <FileVersion>
;   3) src\Neo.Core\Neo.Core.csproj     <Version> / <FileVersion>
;   4) tools\launcher\neo_launcher.rc   FILEVERSION / PRODUCTVERSION / 字符串
;   5) installer\说明.txt               末尾「版本 x.y.z」行
; ============================================================================
#define MyAppVersion "0.2.7"
