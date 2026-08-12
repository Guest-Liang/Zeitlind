# Zeitlind

Zeitlind 是 Windows x64 下的游戏成就导出工具
支持情况：
| 游戏 | 国服 | 国际服 |
| --- | --- | --- |
| 崩坏：星穹铁道 |  🟢 | 🔴 |
| 绝区零 | 🟢 | 🔴 |

## 免责声明与风险提示

> [!WARNING]
> 本项目是与米哈游及相关游戏官方无关的第三方开源工具，仅供个人成就数据备份与迁移使用。工具运行时会向所选游戏进程加载临时 Hook；此类行为可能违反游戏用户协议、运营规则或被反作弊系统识别，并可能导致账号警告、限制、封禁，以及数据或其他损失。
>
> 使用者应在使用前自行了解并遵守所在地法律法规、游戏用户协议及相关规则，自行判断和承担全部风险。项目作者及贡献者不对因下载、安装、运行、修改或传播本工具而产生的账号处罚、封禁、数据丢失、财产损失或其他直接、间接损失承担责任。若无法接受上述风险，请勿使用本工具。

## 使用方法

1. 完全退出正在运行的游戏。
2. 双击运行本程序。
3. 在游戏菜单中选择注册表检测到的安装，或选择需要导出成就的游戏。菜单会直接显示每款游戏的注册表检测状态。
4. 同意程序请求的 Windows 管理员权限。Zeitlind 会在提权后保留已选择的游戏、导出格式和输出目录。
5. 正常登录并进入所选游戏。Zeitlind 捕获到完整成就响应与 UID 后，会先请求本次启动的游戏正常退出；10 秒内没有退出时，才会强制关闭该游戏及其子进程。
6. 选择需要导出的格式：备份、Liyin 或 UIAF。
7. 导出成功后按 Enter 退出。等待成就数据时可按 `Ctrl+C` 取消。

```powershell
# 指定游戏目录
.\Zeitlind_<version>_Release.exe --game "D:\Games\ZenlessZoneZero Game"

# 直接指定 EXE
.\Zeitlind_<version>_Release.exe --game "D:\Games\Star Rail\Games\StarRail.exe"

# 非交互导出；目标目录不存在时会自动创建
.\Zeitlind_<version>_Release.exe --game "D:\Games\Star Rail\Games\StarRail.exe" `
  --format liyin `
  --output "D:\AchievementBackups\2026"
```

完整参数：

```text
Zeitlind.exe [--game "游戏目录或 exe 路径"]
              [--format backup|liyin|uiaf]
              [--output "输出目录"]
Zeitlind.exe --help
Zeitlind.exe --version
```

如果没有 `--game`，交互模式显示游戏选择菜单；输入被重定向时，注册表必须恰好检测到一款游戏，否则需要显式传入 `--game`。`--output` 默认为启动 Zeitlind 时的当前目录；目录不存在时会创建。

## 游戏识别与兼容性检查

| 游戏 | EXE | 注册表路径 |
|---|---|---|
| 绝区零国服 | `ZenlessZoneZero.exe` | `HKCU\Software\miHoYo\HYP\1_1\nap_cn\GameInstallPath` |
| 崩坏：星穹铁道国服 | `StarRail.exe` | `HKCU\Software\miHoYo\HYP\1_1\hkrpg_cn\GameInstallPath` |

## 导出格式

文件名会包含新版名称与游戏 ID，避免两个游戏的结果混在一起：

- `Zeitlind-<game>-achievements-日期时间.json`：Zeitlind v1 备份格式，保留游戏标识、协议探测信息、服务端记录及尚未解释的原始字段；
- `Zeitlind-<game>-liyin-日期时间.json`：对应游戏的 Liyin 导入格式；
- `Zeitlind-<game>-uiaf-日期时间.json`：对应游戏的 UIAF 实验格式。

> [!WARNING]
> 程序日志位于可执行文件所在目录，文件名为 `Zeitlind-YYYY-MM-DD.log`。日志可能包含游戏路径、导出路径和 UID，并且不会自动删除；向他人分享日志前，请先检查其中是否包含不希望公开的个人信息。

运行时提取的 Hook DLL 位于本次运行专用的受保护临时目录；导出成功、取消或发生可处理异常后会自动删除。若进程被强制终止而来不及清理，下一次运行会尝试清除遗留目录。升级后的首次普通权限启动还会在申请 UAC 前清理旧版本固定路径中遗留的 Hook DLL。

## 致谢与许可证

项目设计参考了 [Yae](https://github.com/HolographicHat/Yae)。感谢 HolographicHat 与 Yae 项目贡献者提供的实现思路。

绝区零元数据来自 [zzz.liyin.space](https://github.com/Ticca-Liyin/zzz.liyin.space)，星铁元数据来自 [liyin.space](https://github.com/Ticca-Liyin/liyin.space)。实验性成就交换格式参考 [UIAF](https://uigf.org/zh/standards/uiaf.html) 及其多游戏分组思路，[提案链接](https://github.com/orgs/UIGF-org/discussions/18)。

本仓库采用 GNU GPL v3，详见 [`LICENSE`](LICENSE)。
