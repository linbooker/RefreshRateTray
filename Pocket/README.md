# RefreshRateTray 便携版（Pocket）

一个文件夹装完所有东西：**不安装、不写注册表（除了可选的开机自启）、拷走即用、删掉即卸载**。
插电源用该屏最高刷新率、用电池用最低刷新率，托盘图标上显示当前刷新率。

## 文件夹里有什么

| 文件 | 用途 |
| --- | --- |
| `RefreshRateTray.exe` | 托盘主程序，双击运行 |
| `RefreshRateTray.cli.exe` | 命令行版（查状态、切档位、自检），可选 |
| `settings.ini` | 设置文件，和 exe 放在一起（第一次改设置时自动写） |
| `app.log` | 日志，运行后自动生成 |
| `README.md` | 就是本文件 |

**所有程序文件、配置、日志都只在这个文件夹里**，不往 `%LOCALAPPDATA%`、`Program Files` 或注册表里塞东西。
把整个文件夹拷到 U 盘或另一台电脑，双击 `RefreshRateTray.exe` 就直接能用。

> ⚠️ 唯一例外：如果你在菜单里开启「开机自启动」，程序会写一条
> `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（值名 `RefreshRateTray (Pocket)`），
> 指向当前这个 exe 的完整路径 —— 这是 Windows 开机自启的唯一方式，关掉该项就会删除。

## 使用

1. 把**整个文件夹**复制到任意普通目录（例如 `D:\Tools\RefreshRateTray-Pocket\`）。
2. 双击 `RefreshRateTray.exe`，托盘出现一个数字 —— 当前刷新率（插电时是最高档）。
3. 拔电源 → 图标变黄、数字变最低档；插回去 → 变绿、数字变最高档。

右键托盘图标可改：立即切换、自动切换开关、暂停 N 分钟、适配器/电池各自的目标档位
（含「自动（最高）」「自动（最低）」和「手动输入…」）、开机自启动、退出。

## settings.ini

```ini
# AcHz      : 插电源时的目标刷新率，0 = 自动取该屏最高档
# BatteryHz : 用电池时的目标刷新率，0 = 自动取该屏最低档
# Auto      : 1 = 按电源自动切换，0 = 只显示不干预
AcHz=0
BatteryHz=0
Auto=1
```

- 菜单里改设置会立即写回这个文件；也可以用记事本直接改，改完重启程序生效。
- 删掉 `settings.ini` 就恢复默认（自动最高 / 自动最低 / 自动切换开）。
- 写具体数字（如 `BatteryHz=60`）就是固定档位；显示器不支持时自动退到最接近的一档。

## 命令行

```
RefreshRateTray.cli.exe --status               :: 电源 / 目标 / 每块屏的当前与目标刷新率
RefreshRateTray.cli.exe --list                 :: 列出每块屏支持的刷新率
RefreshRateTray.cli.exe --set <Hz>             :: 立刻把所有屏切到指定值（不启动托盘）
RefreshRateTray.cli.exe --test                 :: 自检：最低档 -> 最高档 -> 恢复原值
RefreshRateTray.cli.exe --ac <Hz|0>            :: 「插电时」目标，0 = 自动最高
RefreshRateTray.cli.exe --battery <Hz|0>       :: 「电池时」目标，0 = 自动最低
RefreshRateTray.cli.exe --icon-preview a.png   :: 把托盘图标渲染成预览图
RefreshRateTray.cli.exe --help
```

便携版的命令行同样读写同目录的 `settings.ini`，不会碰注册表设置。

## 卸载

1. 右键托盘图标 → 退出；
2. 如果开过开机自启，先点「开机自启动 → 关闭」；
3. 删掉整个文件夹即可。

## 与标准版的区别

| | 标准版（`dist\RefreshRateTray.exe`） | 便携版（本文件夹） |
| --- | --- | --- |
| 设置存放 | `HKCU\Software\RefreshRateTray` | 同目录 `settings.ini` |
| 日志 | `%LOCALAPPDATA%\RefreshRateTray\app.log` | 同目录 `app.log` |
| 开机自启值名 | `RefreshRateTray` | `RefreshRateTray (Pocket)` |
| 安装/卸载 | 需要 install.ps1 / uninstall.ps1（可选） | 拷走即用，删文件夹即卸载 |

两个版本功能完全一样，都支持多显示器、运行时枚举档位、手动输入任意刷新率。两者不能同时运行（共用单实例锁）。