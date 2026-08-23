# UniFi Policy Manager

[![Build](https://github.com/autunn/unifi-policy-manager/actions/workflows/build.yml/badge.svg)](https://github.com/autunn/unifi-policy-manager/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/autunn/unifi-policy-manager)](https://github.com/autunn/unifi-policy-manager/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

UniFi Network 管理与策略工具，严格使用 Ubiquiti 官方 Integration API。

- Windows：C# / .NET 8 / WPF 完整版
- macOS：SwiftUI 原生完整版，与 Windows 版共享官方 API 功能范围和安全写入流程

## 4.x 主要变化

- 当前开发版：按 Network v10.4.57 OpenAPI 接入全部 44 条路径、73 个操作，并为双端增加分组 Sidebar 与官方 API 工作台
- 4.1.1：将 212 条转发域规则真正编译进 EXE，点击“载入内置规则（212）”即可直接使用
- 4.1.0：发布包内置 212 条按服务分类的转发域 CSV，选择文件后即可预览、去重并批量新增
- 4.0.2：改用单元格模板强制策略变更、DNS、ACL、防火墙表格的正文、复选框和操作按钮垂直居中

- 分组侧边栏工作台：工作台、网络资源、策略与安全、基础设施与开发四个导航区
- 新增完整策略基线导入、导出和差异计算
- 一份计划统一管理 DNS、ACL 与防火墙的新增、更新和删除
- 导出的基线包含 ACL 与防火墙用户策略排序
- 执行前自动保存完整快照，可直接载入上次执行前快照生成恢复计划
- 严格同步默认关闭；删除项必须主动开启并手动选择
- 无效或重复项目会自动阻止对应策略范围生成删除项
- 兼容旧版 DNS-only 备份，不会误删 ACL 或防火墙策略

## 支持范围

程序已经逐项接入 UniFi Network v10.4.57 官方 OpenAPI 的全部 73 个操作：

| Sidebar 模块 | 官方操作数 | 能力 |
| --- | ---: | --- |
| 应用与站点 | 2 | 应用信息、本地站点 |
| UniFi 设备 | 8 | 待采用/已采用设备、采用、移除、设备与端口动作、统计 |
| 在线客户端 | 3 | 客户端列表、详情、访客授权动作 |
| 网络 | 6 | 列表、创建、详情、更新、删除、引用 |
| WiFi 广播 | 5 | 完整 CRUD |
| Hotspot 凭证 | 5 | 生成、查询、单个与批量删除 |
| 防火墙 | 13 | 策略、Patch、按区域排序、自定义区域 |
| ACL | 7 | 规则 CRUD 与排序 |
| 交换与聚合 | 6 | LAG、MC-LAG Domain、Switch Stack |
| DNS Policy | 5 | 完整 CRUD |
| 流量匹配列表 | 5 | IPv4、IPv6、端口列表 CRUD |
| 支持资源 | 8 | 国家、DPI、标签、RADIUS、VPN、WAN |

Windows 与 macOS 都提供“全部官方端点”入口，并支持操作搜索、自动填充当前 Site ID、路径参数、查询参数、JSON 请求体、响应复制和写操作二次确认。防火墙排序与 Hotspot 批量删除等端点会明确提示官方必填查询参数。

常用 Policy Table 类型继续使用更易操作的专用页面：

- ACL 规则：列表、新增、编辑、启停、删除、排序
- DNS 记录：转发域名、A、AAAA、CNAME、MX、TXT、SRV 的完整 CRUD
- 防火墙策略：列表、新增、编辑、启停、删除、排序

官方 Network v10.4.57 OpenAPI 当前没有 NAT、基于策略的路由、端口转发、QoS、静态路由接口，因此程序不提供这些类型，也不会调用控制器内部接口或 SSH。

## 直接使用

### Windows

双击：

```text
publish-4.1.1\UniFi-Policy-Manager.exe
```

### macOS

macOS 14 或更高版本可从源码构建原生 SwiftUI 应用：

```bash
./macos/build-app.sh
open macos/dist/UniFi-Policy-Manager.app
```

macOS 端使用系统钥匙串保存 API Key，并在写入前将完整基线保存到
`~/Library/Application Support/UniFiPolicyManager/backups`。macOS 已包含策略变更中心、
212 条内置规则、TXT/CSV/XLSX 批量导入、批量删除和策略排序。完整说明见 [`macos/README.md`](macos/README.md)。

连接时填写：

- UCG 地址，例如 `192.168.1.1`
- 在本地 Console → `Integrations`，或 `unifi.ui.com → Settings → API Keys` 创建的 API Key
- UCG 使用自签名证书时不要勾选“验证 HTTPS 证书”
- Windows 版使用 DPAPI 按当前用户加密保存 API Key；macOS 版使用系统钥匙串

程序调用 `/proxy/network/integration/v1/info` 验证 API Key，并自动读取 Site UUID。多站点环境会显示站点选择窗口。官方 API 工作台只允许 `/v1/` 路径，并统一映射到 Console 的 `/proxy/network/integration` 前缀。

## 策略变更中心

1. 点击“导出当前基线”，保存当前 DNS、ACL、防火墙和排序。
2. 修改或选择另一个基线 JSON，然后点击“载入 JSON”。
3. 程序计算新增、更新、删除、不变和无效项目。
4. 默认只选择新增与更新；开启“严格同步”后才会生成删除项，并恢复用户策略排序。
5. 检查并手动选择需要执行的项目，再点击“执行所选变更”。

写入通过官方端点逐条完成。执行前保存的快照位于：

```text
%LOCALAPPDATA%\UniFiPolicyManager\backups
```

执行一次变更计划后，可以点击“载入上次快照”生成恢复计划。恢复仍需预览和确认，不会绕过安全检查。

跨站点导入会按策略 ID 或名称匹配；网络和防火墙区域 UUID 必须在目标站点有效。

## ACL 与防火墙编辑

ACL 和防火墙使用完整 JSON 请求体编辑器，以覆盖官方 Schema 的组合字段：

- 新增策略提供官方字段模板，默认 `enabled: false`
- 编辑时自动移除 `id`、`index`、`metadata` 等只读字段
- 保存前检查必填字段、动作类型、IP 版本及引用 UUID
- 编辑器显示 Networks、Firewall Zones 等参考 UUID
- 系统定义和派生策略只读；只有 `USER_DEFINED` 策略允许修改和删除

## DNS 批量功能

批量新增支持官方 DNS API 的全部 7 种记录：

- 转发域名（`NS` 或 `FORWARD_DOMAIN`）、A、AAAA、CNAME、MX、TXT、SRV
- 导入 TXT、CSV、XLSX；旧式“一行一个域名”清单仍按转发域名导入
- 内置 Excel 兼容 CSV 模板，包含 TTL、优先级、权重、端口、服务、协议和启用状态
- 自动规范化、验证、去重并跳过已存在记录
- 批量删除目前仍只限转发域名

EXE 内部直接封装了 212 条按服务分类的转发域规则，不依赖外部 CSV 文件。内置规则的 DNS 服务器留空，使用时：

1. 在“转发域默认 DNS 服务器”中填写自己的 DNS 服务地址；如果已有转发域，程序会尝试自动填充。
2. 点击“载入内置规则（212）”。
3. 检查导入统计，然后点击“预览并新增”。
4. 程序会跳过已存在规则，并在正式新增前显示完整预览。“选择外部规则文件”仍可导入自定义 TXT、CSV 或 XLSX。

## 安全

- API Key 使用 Windows DPAPI 当前用户加密，设置文件中不保存明文
- API Key 不写入策略基线、快照或操作日志
- DNS、ACL、防火墙专用页面修改前自动备份完整策略基线
- 通用官方 API 工作台写操作显示最终路径与请求体并二次确认；审计日志只记录 operation ID 和方法，不记录请求体或查询值
- Windows 操作日志位于 `%LOCALAPPDATA%\UniFiPolicyManager\logs\operations.ndjson`
- 系统/派生策略保持只读
- 不使用 SSH，不访问未公开的控制器内部接口

## 一键打包与发布

仓库的 GitHub Actions 分工如下：

- `Windows CI` 与 `macOS CI` 只负责提交和 Pull Request 的自动编译检查。
- 手动发布时只运行 `Package & Release`，不要分别运行两套 CI。

操作步骤：

1. 打开 GitHub 仓库的 `Actions` → `Package & Release` → `Run workflow`。
2. 输入不带 `v` 的版本号，例如 `4.2.0`；创建 Release 时不能与已有 Tag/Release 重复。
3. 根据需要选择是否标记为预发布；如果只想下载 Actions Artifacts、不创建 Release，可关闭“创建 GitHub Release”。
4. 工作流会并行构建 Windows 与 macOS；Windows 还会运行完整自测。
5. 两端全部成功后，自动创建 `v版本号` 的 GitHub Release，并同时上传：
   - `UniFi-Policy-Manager-版本号-win-x64.zip`
   - `UniFi-Policy-Manager-版本号-macOS.zip`
   - `SHA256SUMS.txt`

任一平台构建或自测失败时不会创建 Release。

发布工作流默认不要求购买代码签名证书：Windows 会生成未签名 EXE，macOS 会生成启用 Hardened Runtime 的 ad-hoc 签名应用。配置证书后，工作流会自动执行 Windows Authenticode、macOS Developer ID 签名和 Apple 公证。具体配置和免费/付费选择见 [`SIGNING.md`](SIGNING.md)。

## 演示与自测

```powershell
.\publish-4.1.1\UniFi-Policy-Manager.exe --demo
```

```powershell
Start-Process .\publish-4.1.1\UniFi-Policy-Manager.exe -ArgumentList '--self-test','--self-test-output=self-test.json' -Wait
```

## 构建

开发环境：Windows 10/11 与 .NET 8 SDK。

```powershell
.\build.ps1
```

输出：

```text
publish-4.1.1\UniFi-Policy-Manager.exe
UniFi-Policy-Manager-4.1.1-win-x64.zip
```

也可以直接使用标准 .NET 命令：

```powershell
dotnet restore .\UniFiDnsManager.csproj
dotnet build .\UniFiDnsManager.csproj -c Release --no-restore
```

仓库内置 GitHub Actions 工作流。每次推送或 Pull Request 都会在 Windows 环境构建项目，并生成自包含的 `win-x64` EXE 构建产物。

## 仓库安全

- 不要提交真实 API Key、连接设置、策略快照、操作日志或包含私人网络信息的截图
- `.gitignore` 默认排除本地 SDK、构建目录、发布包、日志、备份和环境文件
- 提交前建议运行 `git status`，确认暂存区只包含源码和文档
