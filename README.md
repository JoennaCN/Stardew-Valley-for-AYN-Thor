<p align="center">
  <img src="dualscreen/VerticalWorld/assets/ZeroFrameLogo.png" width="180" alt="ZeroFrame LAB">
</p>

# Stardew Valley for AYN Thor

让 Steam 原版《星露谷物语》在 AYN Thor + ArmadaOS 上以真正的双屏方式运行。

上屏与下屏共享同一个连续世界：人物可以从上屏走到下屏；对话、商店与快捷菜单会按场景重新布局。下屏也能切换为地图、背包、任务、关系和设备设置页面。游戏仍从 Steam 中直接启动，不包含或替代游戏本体。

> 非官方社区项目 · for AYN Thor · by ZeroFrame LAB

## 主要功能

- 针对 AYN Thor 上屏 `1920×1080`、下屏 `1240×1080`（游戏方向）适配。
- 单次世界渲染后输出到两块屏幕，人物可跨越上下屏移动。
- 下屏使用 ArmadaOS DRM lease 与 DMA-BUF 原生显示路径，目标 60 FPS。
- 标题、存档、角色创建、对话、商店、钓鱼和快捷菜单双屏布局修正。
- Select 切换连续世界与下屏信息页；下屏支持触摸操作。
- 上下屏亮度、色温和色调可分别调节。
- 针对 FEX 的首次路径预热与移动期间低延迟垃圾回收策略。
- 游戏退出后自动恢复 ArmadaOS 下屏桌面。

## 下载

请从 [GitHub Releases](https://github.com/JoennaCN/Stardew-Valley-for-AYN-Thor/releases) 下载：

```text
Stardew-Valley-for-AYN-Thor-0.6.3-reference-v2.zip
```

同时下载 `.sha256` 文件可以校验安装包。GitHub 自动生成的 **Source code** 压缩包只是源码，不是可安装版本。

## 使用要求

- AYN Thor，运行 ArmadaOS。
- Steam 正版 Stardew Valley，当前适配基于 Windows 版 `1.6.15`。
- SMAPI `4.5.2` 或更高版本，并已设为 Steam 启动目标。
- 已启用 ArmadaOS 实验性下屏桌面/显示服务。

本项目不包含 Stardew Valley 游戏文件、存档、Steam 凭据或第三方 MOD。

## 安装

1. 在 Thor 上解压发布包。
2. 保存进度并完全退出 Stardew Valley。
3. 在解压目录打开终端，运行：

```bash
chmod +x install.sh uninstall.sh
./install.sh
```

4. 根据提示输入系统密码。
5. 回到 Steam，像平常一样启动 Stardew Valley。

安装程序会自动定位 Steam 游戏目录、备份旧版插件、安装下屏桥接服务，并只修改 App ID `413150` 对应的 ArmadaOS 游戏环境。不会修改游戏内容或存档。

如果 Steam 库不在默认位置：

```bash
STEAM_GAME_DIR='/你的路径/Stardew Valley' ./install.sh
```

## 操作

| 操作 | 功能 |
|---|---|
| Select | 切换连续双屏世界 / 下屏信息页 |
| 下屏触摸 | 操作下屏菜单、地图和设置 |
| L / R | 在支持的页面切换背包行或栏目 |
| 长按 Select | 紧急退出双屏界面 |

普通暂停、工具、对话及游戏操作保持原版手柄逻辑。

## 显示模式

### 连续世界

上下屏显示同一张纵向延伸的游戏世界。人物靠近屏幕分界线时会自然跨屏，不是简单复制画面，也不是独立的伴侣程序。

### 下屏信息页

上屏保留游戏、对话和快捷菜单，下屏可显示背包、制作、任务、关系、地图、田地状态、机器、献祭、日历及设置。

## 性能说明

当前版本使用单次世界绘制、三缓冲 DMA-BUF 提交和原生同步栅栏。常规场景目标为 60 FPS。首次进入地点或第一次触发某些原版逻辑时，FEX 翻译、资源加载或 .NET 回收仍可能造成短暂掉帧；项目已对农场、公交站、城镇和移动路径加入预热与低延迟处理，但不承诺所有 MOD 组合始终锁定 60 FPS。

公开安装包默认关闭帧时间诊断，不会持续写入性能日志。

## 已知限制

- 目前只针对 AYN Thor + ArmadaOS 实机开发和验证。
- SteamOS、其他双屏掌机及不同分辨率设备尚未完成适配。
- 某些大型内容包、高清肖像或改变 UI 的 MOD 可能需要额外兼容处理。
- 系统或 Steam 更新可能改变 FEX、gamescope、DRM lease 或下屏服务行为。
- 睡觉保存前强制退出游戏仍会丢失当天进度。

## 排错

如果下屏仍显示 Plasma 桌面：

```bash
systemctl --user status stardew-thor-bridge.service
```

如果游戏启动后立即退出，请检查：

```text
~/.config/StardewValley/ErrorLogs/SMAPI-latest.txt
Steam/steamapps/common/Stardew Valley/Mods/VerticalWorld/vertical-world.log
Steam/steamapps/common/Stardew Valley/Mods/VerticalWorld/vertical-bridge.log
```

提交 Issue 时请注明 ArmadaOS 版本、游戏版本、复现地点和操作步骤。公开日志前请删除用户名、存档名称和其他个人信息。

## 卸载

完全退出游戏后，在安装包目录运行：

```bash
./uninstall.sh
```

插件目录会被移动为带时间戳的备份，而不是直接删除。卸载程序不会自动清理整个 `game-tweaks.json`，以免覆盖用户的其他自定义设置。

## 从源码构建

需要 .NET 6 SDK，以及来自你本人合法安装副本的 Stardew Valley / SMAPI 编译引用：

```bash
dotnet build dualscreen/VerticalWorld/VerticalWorld.csproj \
  -c Release \
  -p:GamePath='/path/to/Stardew Valley' \
  -p:SmapiPath='/path/to/Stardew Valley'
```

编译输出位于 `dualscreen/VerticalWorld/bin/Release/net6.0/`。游戏 DLL 只用于本地编译引用，不应提交或重新分发。

## 项目结构

```text
dualscreen/VerticalWorld/   SMAPI 双屏插件
armada/thor/native/         ArmadaOS 下屏桥接与 DMA-BUF 显示
armada/thor/systemd/        用户级桥接服务
packaging/thor/             AYN Thor 安装与卸载脚本
docs/                       Thor 显示配置说明
```

## 致谢与声明

本项目源自 [LPF970915/Stardew-Valley-for-RGDSplus](https://github.com/LPF970915/Stardew-Valley-for-RGDSplus) 的双屏适配工作，并针对 AYN Thor / ArmadaOS 重新设计显示与输入链路。

下屏控制代码部分思路与实现改编自 Daniel Vergara 的 `decky-digimon-sts-team-builder`，相关部分按 BSD-3-Clause 使用，见 `armada/thor/LICENSE.bottom-screen`。

Stardew Valley 名称、游戏内容和商标归其权利人所有。本项目不隶属于 ConcernedApe、Steam、AYN 或 ArmadaOS 团队，也不提供游戏本体。
