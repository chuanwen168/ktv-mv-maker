# KTV 字幕 MV 制作工具（双排歌词）

基于 **VC# / .NET Framework 4.0**（VS2010 Express 兼容）开发的 KTV 歌词 MV 制作工具。
读取酷狗 KRC/KSC 歌词文件（含逐字时间轴），叠加到视频上，一键合成带 **KTV 双排歌词**效果的 MV 视频。

---

## 功能特性

- **KRC 歌词解密**：通过 GitHub 开源项目移植的 `KugouDecoder.dll` 插件解密酷狗 KRC 歌词（支持新版 KRC XML 格式与旧版 KSC 逐字格式）。
- **交替双排歌词**：上下两排等大字号，白字深描边 + **卡拉OK逐字金色渐变**；
  上排**左对齐**、下排**右对齐**（KTV 点歌机风格）：
  - 偶数句：上排卡拉OK主句，下排全程显示下一句预告；
  - 奇数句：下排卡拉OK主句，上排延迟（句长 × 0.4，下限 500ms）后切换显示下一句预告；
  - 字幕组置于画面底部（距底边 8%）。
- **信息行静态显示**：歌曲信息行（"歌名 - 歌手"、作词作曲、编曲/混音/母带/和声等）在歌曲开头
  以**静态白字**显示（不参与卡拉OK变色），标题显示在上排、作词作曲在下排，正式歌词开始前无缝衔接。
- **歌词样式自定义**：字号滑杆（24–140px，按 720p 基准随视频分辨率等比缩放）+
  **未唱部分颜色 / 已唱（卡拉OK）部分颜色**分别自定义（点击色块弹出系统选色器，上下排同色）；
  预览与合成均即时生效。
- **实时预览**：**LibVLC 内嵌播放**（自带解码器，不依赖系统），全格式直放，进度条可拖；
  歌词以 **ASS 字幕经 libvlc 渲染在视频画面内**（与合成完全一致的效果）。
- **一键合成 MV**：调用 `D:\KTVmaker\ffmpeg.exe`（libass）把卡拉OK字幕烧录进视频，输出 MP4/MKV（H.264），画质可调，音频无损复制。
- **插件化设计**：解密 DLL 独立于主程序，可单独更新。

---

## 目录结构

```
D:\KTVmaker\                        ← 固定资源目录
├─ ffmpeg.exe / ffprobe.exe         ← 视频合成引擎（libass 烧录字幕）
├─ plugins\                         ← 插件目录
│   ├─ KugouDecoder.dll             ← KRC 解密插件（编译后自动复制到此）
│   └─ ICSharpCode.SharpZipLib.dll  ← 插件依赖（zlib 解压）
├─ libvlc\                           ← LibVLC 3.0.24（内嵌预览播放引擎，x64）
│   ├─ libvlc.dll / libvlccore.dll    ← 播放器核心（P/Invoke）
│   └─ plugins\                       ← 全格式解码器插件（必须与 dll 同目录）
├─ mv_tmp\                          ← 合成临时目录（ASS 字幕等，自动创建）
├─ third_party\                     ← 开源项目源码与第三方库（下载/解压位置）
└─ KTV-MV-maker\                    ← 项目目录（固定）
    ├─ KTV-MV-maker.sln             ← VS2010 解决方案（含两个项目）
    ├─ KTV-MV-maker\                ← 主程序（WPF）
    │   ├─ Window1.xaml(.cs)        ← 主窗口
    │   ├─ Lyrics\                  ← 歌词模型/解析/渲染
    │   ├─ Export\                  ← ASS 字幕生成 / ffmpeg 合成
    │   └─ Plugin\                  ← 插件反射加载器
    ├─ KugouDecoder\                ← KRC 解密插件类库（→ KugouDecoder.dll）
    └─ README.md
```

---

## 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows（已实测 Windows 10/11） |
| 运行时 | .NET Framework 4.0 或更高 |
| 开发工具 | Visual Studio 2010 Express 或更高（可选，命令行也可编译） |
| 视频引擎 | `D:\KTVmaker\ffmpeg.exe`、`ffprobe.exe`（必须带 libass 的构建，如 gyan.dev essentials） |
| 预览引擎（核心） | `D:\KTVmaker\libvlc\`（LibVLC 3.0.24：libvlc.dll + libvlccore.dll + plugins\） |
| 预览引擎（备用） | `D:\KTVmaker\ffplay.exe`（ffmpeg 套件自带播放器，可选） |

---

## 编译方法

### 方式一：VS2010 Express
用 VS2010 打开 `D:\KTVmaker\KTV-MV-maker\KTV-MV-maker.sln`，直接生成（F6）即可。
生成后自动把 `KugouDecoder.dll` 复制到 `D:\KTVmaker\plugins\`。

### 方式二：命令行
```bat
"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" ^
  "D:\KTVmaker\KTV-MV-maker\KTV-MV-maker.sln" /t:Build /p:Configuration=Release
```

---

## 使用步骤

1. 启动主程序：`D:\KTVmaker\KTV-MV-maker\KTV-MV-maker\bin\Release\KTV-MV-maker.exe`
2. 「视频文件」选择要制作 MV 的视频（mp4/mkv/avi/wmv 等）；
3. 「KRC歌词」选择酷狗下载的 `.krc` 歌词文件，点击 **加载歌词**；
4. 点击 **播放** 预览双排歌词效果（拖进度条可快速检查）；
5. 设置输出文件、格式（MP4/MKV）、画质，确认 `ffmpeg` 路径后点击 **开始合成 MV**；
6. 等待进度条走完，弹出合成完成提示，即可在输出路径查看成品。

> **预览说明**：
> - **内嵌预览（默认）**：LibVLC 在工具窗口内解码播放，**不依赖系统解码器**，
>   全格式直放（mp4/mkv/avi/wmv/mov/flv 等），进度条可拖动、歌词实时同步；
>   播放库位于 `D:\KTVmaker\libvlc\`（缺失时仅预览不可用，合成不受影响）。
> - **外部预览（备用按钮）**：点击「外部预览」用 `ffplay.exe` 独立窗口播放（可播所有格式，
>   含实时卡拉OK字幕）。把 ffplay.exe 放入 `D:\KTVmaker\` 即可启用该按钮。

---

## 技术要点

### KRC 解密算法（来自开源项目）
文件头 `krc18` 之后的数据，用 16 字节密钥 `{0x40,0x47,0x61,0x77,0x5e,0x32,0x74,0x47,0x51,0x36,0x31,0x2d,0xce,0xd2,0x6e,0x69}` 逐字节异或，再 zlib 解压即得明文：
- 新版 KRC：`<krc><timeline><s p="句开始"><l t="字偏移">字</l>...</s></timeline></krc>`
- 旧版 KSC：`[句开始,句时长]<字偏移,字长,0>字...`

### 卡拉OK字幕（ASS + libass）
- 把逐字时间轴转为 ASS 的 `{\kf<厘秒>}` 标签，libass 按字填充金色渐变；
- 交替双排用两个样式：`LyricTop`（`\an4` 左中，左边距）与 `LyricBottom`（`\an6` 右中，右边距），字号等大（高度 × 0.07）；
- 偶数句上排主句卡拉OK + 下排全程预告下一句；奇数句下排主句卡拉OK + 上排延迟（句长 × 0.4，下限 500ms）切换预告；
- `{\fs}` 按两句较长者统一自适应字号；预览（libvlc add_slave）与合成（ffmpeg ass=）共用同一 `AssBuilder`，效果一致。

### ffmpeg 合成命令（核心）
```
ffmpeg -y -i 输入视频 -vf ass=subtitle.ass -c:v libx264 -preset medium -crf 20
       -pix_fmt yuv420p -c:a copy -movflags +faststart 输出.mp4
```
（ASS 生成在 `mv_tmp\`，进程工作目录设为该目录，避免路径转义问题；进度通过 `-progress pipe:1` 解析。）

### LibVLC 内嵌预览（不依赖系统解码器）
- `VlcPlayer.cs`：P/Invoke 调用 `libvlc.dll`，`VlcHost`（HwndHost 子窗口）接收视频画面，嵌入 WPF 窗体；
- 播放前 `SetDllDirectory(D:\KTVmaker\libvlc)`，保证 plugins\ 解码器被加载（dll 与 plugins 必须同目录）；
- 进度条定时器每 80ms 读 `libvlc_media_player_get_time/get_length` 驱动滑块与歌词叠加层；
- 拖动进度条 → `libvlc_media_player_set_time` 跳转，字幕随视频时间轴同步；
- 本机无 Windows Media Player 解码器（MediaElement 无法播任何视频）是改走 LibVLC 的根本原因。

---

## 开源致谢与许可

- **WXRIW/Kugou-Lyrics-Decoder**（MIT License）——KRC 解密算法来源。
  仓库：https://github.com/WXRIW/Kugou-Lyrics-Decoder
  许可文本见 `KugouDecoder\LICENSE-MIT.txt`。
- **ICSharpCode.SharpZipLib** 0.86.0.518 —— zlib 解压（GPL with classpath exception）。
- **FFmpeg**（gyan.dev essentials build）——视频合成引擎（GPL/LGPL 按构建配置）。
- 本工具仅用于处理用户合法持有的歌词与视频文件。

---

## 已知限制

- 内嵌预览需 `D:\KTVmaker\libvlc\` 完整存在（libvlc.dll + plugins\，缺失时预览不可用，合成不受影响）；
- 合成视频时长与源视频一致；音频以原格式直接复制；
- 若 KRC 无逐字时间（纯 LRC），整句按渐变方式显示。
