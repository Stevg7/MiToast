# MiToast 客户端「本地总结」功能实现计划

## 目标与边界

- Windows 客户端新增"本地总结"：从本地历史通知（HistoryService）聚合出结构化 digest，调本地 llama.cpp（CPU）生成中文总结，应用内查看/复制。
- 引擎与模型**客户端自动下载**（llama.cpp GitHub Release + hf-mirror.com 的 Qwen3-4B-Instruct Q4_K_M GGUF，约 2.6GB，存 `%AppData%\MiToast\LocalLLM\`），无需用户手动找文件。
- 触发方式**仅手动**（总结窗口里的"立即生成"按钮），不做应用内定时；现有 ZCode 每 3 天云端任务**保持不动**，MCP 接口路径也不动（作为不支持本地推理设备的保留路径）。

## 新增文件（均在 windows/src/MiToast/）

1. **`Models/SummaryEntry.cs`** — 总结记录：GeneratedAt、RangeStart/RangeEnd(ms)、TotalCount、SummaryMd、DigestMd（留档原始 digest 便于排查）。
2. **`Services/Summarizer/DigestBuilder.cs`** — 确定性聚合（规则做数据，模型只行文）：
   - 输入 `HistoryService.GetAll()` 按窗口过滤（默认 72h，设置可选 24/72/168）；
   - payment：正则提金额（¥/人民币/…元），关键词分方向（退款/收款/支出），同金额±3min 跨渠道去重合并，输出净额+逐笔行（时间/应用/金额/方向/备注≤30字）；
   - verification：只计数+涉及应用列表，验证码本身绝不进 digest；promo：计数+Top3 应用；
   - delivery/pickup/order：逐条一行（应用+标题/内容截断≤50字），取餐码保留（行动项用）；
   - schedule：逐条带时间；chat：按应用计数+每应用≤6条样本（内容≤60字）；music/progress/system/general：仅计数；
   - 头部：时间范围+总数+分类计数。目标 ≤6KB。
3. **`Services/Summarizer/LlamaServerHost.cs`** — llama-server 进程与 HTTP：
   - 随机空闲端口启动 `llama-server.exe -m <model> --host 127.0.0.1 -c 12288 -ngl 0`（隐藏窗口）；
   - 轮询 `/health` 就绪（超时 90s）；POST `/v1/chat/completions`（temperature 0.3，max_tokens 1200，读超时无限+可取消）；用完 Kill 整个进程树；
   - system prompt 固化现有摘要约定：总体概况 2-3 句→分类分组→单列「需要关注/行动」→推广与验证码一笔带过→隐私转述脱敏。
4. **`Services/Summarizer/EngineInstaller.cs`** — 自动下载与安装：
   - 状态机：未下载/下载中/就绪/失败（含失败原因）；资产 URL 为常量（pin 一个已知可用的 llama.cpp release zip + hf-mirror 的 Qwen3-4B Q4_K_M 单文件）；
   - 模型下载支持 HTTP Range 断点续传，流式 SHA256 校验（与 HF 卡片公布的哈希比对）；引擎 zip 完整后 `ZipFile` 解压出 llama-server.exe 及 DLL；
   - 对外 `EnsureInstalledAsync(progress, ct)` 与 `GetStatus()`，进度回调报字节/总字节。
5. **`Services/Summarizer/SummaryService.cs`** — 编排与持久化：
   - `SemaphoreSlim(1,1)` 防重入；`RunAsync(hours, ct)`：EnsureInstalled → 起 server → DigestBuilder → 请求 → 存 `%AppData%\MiToast\summaries.json`（DPAPI 加密，脱敏后的总结仍含私人信息）→ Changed 事件；中途可取消（按钮再点一次=取消）；
   - `TestEngineAsync()`：起 server→/health + /v1/models→停，报告模型名。
6. **`Windows/SummaryWindow.xaml(.cs)`** — 单例窗口（复用 HistoryWindow 的 ShowSingleton + 主题 + HistoryLock 验证门）：
   - 顶栏：时间范围 ComboBox（24/72/168h）+「立即生成」PrimaryButton（生成中显示已用时，再点取消）+「复制 Markdown」；
   - 下方列表显示历史总结（日期段+生成时间），点选后下方展示正文（轻量 Markdown 渲染：标题加粗放大、列表转圆点、粗体，纯 TextBlock 组合，不引第三方库）。
7. **重构 `Services/NotificationText.cs`** — 把 `MiFocusNotification.ExtractVerificationCode` 与取餐码正则移为公共静态工具（HistoryItem/MiFocusNotification 改为委托），DigestBuilder 复用，避免 Services→UI 反向引用。

## 修改文件

- **`Services/AppSettings.cs`** — 新增 `LocalSummaryWindowHours`（默认 72，Normalize 钳 6-720）。路径不进设置：引擎目录固定 `%AppData%\MiToast\LocalLLM\`（自动下载管理，%AppData% 不进 git 无需 gitignore）。
- **`Services/SensitiveStorage.cs`** — 抽出通用 `ReadProtectedText(path)` / `WriteProtectedBytes(path, bytes)`，现有 History 方法改为委托（行为不变），summaries.json 复用。
- **`Windows/SettingsWindow.xaml(.cs)`** — 「勿扰模式」与「历史记录」之间插入「本地总结」SectionCard：状态行（引擎/模型状态或失败原因）+「下载引擎与模型」按钮（含进度百分比，完成后自动转测）+「测试引擎」按钮（报就绪/模型名）+「打开总结窗口」按钮；沿用 `_loading` 防护与即时 Save 模式。
- **`README.md`** — 新章节：功能说明、首次使用（点下载，约 2.6GB，失败时的重试）、CPU 推理预期（生成约 2-4 分钟）、与 MCP 查询路径的关系。

## 不做 / 保持不变

- 不做应用内定时总结；不改 ZCode 定时任务；不改 MiToastMcp；不引第三方 NuGet 依赖（下载/解压/HTTP 全用 BCL）。

## 验证

1. `dotnet build` 通过；DigestBuilder 用合成历史数据自测（临时控制台注入或单测式脚本）核对金额对冲/去重/脱敏输出。
2. 在本机通过设置页实际下载引擎+模型，跑「测试引擎」确认加载成功。
3. 注入合成通知后手动生成一次总结，核对：过程状态反馈、结果入库、窗口显示、复制导出、取消按钮。
4. 全程不依赖云端；MCP 查询路径回归确认不受影响。
