# 构建、测试与发布

## 开发环境

Windows 11 x64、PowerShell、Git，以及.NET 10 SDK。可使用支持.NET 10的IDE，或直接使用命令行。WPF完整界面验证应在Windows会话内运行。

`build.ps1`在缺少仓库私有SDK时通过微软安装脚本获取10.0通道SDK到`.tools/dotnet`。该目录不会提交。构建脚本下载SDK和发布依赖需要联网，软件正常处理素材无需联网。

```powershell
git clone https://github.com/cz15909681360-create/VideoArchive.git
Set-Location VideoArchive
./build.ps1 -Test
```

已有SDK也可直接运行：

```powershell
dotnet build src/VideoArchive/VideoArchive.csproj -c Release
dotnet run --project tests/VideoArchive.Tests -c Release
```

测试项目是控制台运行器，不使用`dotnet test`发现测试。失败抛出异常并返回非零退出码。

## 媒体验收

完整集成测试需要PATH中的FFmpeg和FFprobe。本机基线采用Gyan.dev FFmpeg 8.1.1 full build；检查版本和编码器列表后再运行。

```powershell
ffmpeg -version
ffprobe -version
./build.ps1 -Integration
```

测试生成自己的素材到`artifacts/`，覆盖CPU、NVIDIA、Intel实际编码、HDR、VFR、时间码、字幕、多音轨、目录镜像和恢复等。完整硬件矩阵依赖本机设备，普通GitHub托管Windows runner不具备本项目验收用GPU；CI只执行构建和核心检查，不宣称GPU编码通过。

运行源程序时，FFmpeg/FFprobe从应用同目录`tools`或PATH寻找，也可在设置指定。调试界面可运行：

```powershell
dotnet run --project src/VideoArchive -c Release
```

开发测试需要隔离本机配置时，在当前进程设置`VIDEOARCHIVE_TEST_DATA`为独立目录；不设置时使用`%LOCALAPPDATA%\VideoArchive`。

## Windows便携包

打包脚本当前按Gyan.dev FFmpeg 8.1.1安装布局收集许可证、发行版README和对应源码归档，要求FFmpeg/FFprobe位于同一发行版的`bin`目录。使用其他发行版时，应先调整第三方资料收集与版本，而不是仅替换文件名。

```powershell
./build.ps1 -Test -Package -ReleaseName VideoArchive-v1.3-win-x64
```

输出位于`artifacts/`：

- `VideoArchive-v1.3-win-x64/`及同名ZIP：Windows x64自包含程序、运行时、`tools/`、`licenses/`和说明文档。
- `VideoArchive-source.zip`：源码、测试、构建脚本、文档及仓库配置，不含SDK、构建中间文件和用户数据。

程序源码MIT许可证与第三方组件许可证分别适用；发布包需保留第三方资料。详情见[第三方声明](../THIRD-PARTY-NOTICES.md)。当前程序未签名。

## 发布检查

1. 更新项目版本、CHANGELOG、README下载说明和验收记录。
2. 运行核心检查；媒体策略改动还需执行相关集成与真实设备验收。
3. 从ZIP解压到独立目录，验证不依赖外部.NET/FFmpeg；检查界面、缓存、预设和任务恢复。
4. 核对包内许可证、第三方资料，移除测试素材、个人配置、原始日志和调试符号。
5. 生成SHA-256摘要，创建版本标签和GitHub Release，上传便携ZIP及摘要；不要将可执行文件或SDK提交进Git历史。

自动构建定义见[build.yml](../.github/workflows/build.yml)，采用GitHub官方[checkout](https://github.com/actions/checkout)与[setup-dotnet](https://github.com/actions/setup-dotnet)。
