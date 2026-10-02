# AI Gateway Setup Protocol v1

状态：v1 实现合同候选，2026-10-02；尚未发布或成为行业标准。
本文件使用虚构服务和凭据，定义公开实现合同。
协议工作名为 AI Gateway Setup Protocol（AGSP）；规范中的“必须”表示互操作要求。

## 1. 目的与范围

用户在服务商网站先选择服务分组及密钥，再复制一个自包含安装码。
通用助手只凭安装码中明确的地址连接该服务；认证成功后领取配置并安装已支持的客户端。
公开的是协议与实现，服务配置必须经过鉴权才能读取。

任何网关都可以实现协议或部署自己控制的适配层，不需要中央服务商注册表。
没有分组概念的网关可映射为一个默认服务，不要求复制其他产品的数据库模型。
支持模型 API 不等于支持本协议；实现本协议也不等于兼容所有模型协议或桌面应用。
v1 客户端适配范围为 Windows Codex、API Key 认证及 Responses API；
协议保留应用标识以便后续扩展，不接受未知应用的动态执行指令。

不使用文件名、下载来源记录、浏览器插件或额外配置文件识别服务。
不通过匿名 manifest、Logo 请求或端点探测绕过认证。
缺少安装码时，只能展示中立界面及静态说明，不能发起服务发现。

## 2. 信任边界

| 对象 | 权威来源 | 不代表什么 |
| --- | --- | --- |
| 初次连接目标 | 用户从可信服务页面复制的地址，HTTPS 校验及界面域名确认 | 安装码中的名字不是服务身份认证 |
| 用户、分组、密钥及权限 | 服务端保存的安装记录及当前授权状态 | 不接受安装码自行声明的额外权限 |
| 客户端安装身份 | 签名助手内的应用适配器及其可信发行规则 | 服务端不能自行指定可信发布者 |
| 网站名称、Logo、镜像 | 通过鉴权取得的服务商数据 | 不获得脚本执行或修改系统策略的权限 |

助手只能离线检查格式；票据合法性必须由服务端判断。
格式正确但无效的票据会产生一次认证请求，但不得得到任何发现内容。
合法票据仍是 bearer secret：被盗后可能被抢先认领，后述恢复秘密不能解决首次抢兑。
助手签名不证明第三方 API 服务可靠，也不证明某个网络请求来自原版助手。

## 3. 安装码

传输格式为 `AGSP1.` 加无填充 Base64url 编码的 UTF-8 JSON，不压缩。
编码用于复制，不加密、不签名；界面必须按凭据保护整个安装码。
安装码总长上限 16 KiB，解码后 JSON 上限 8 KiB；拒绝重复键、无效 UTF-8、
未知主版本及未知凭据类型。仅可清理整段文本首尾空白，不猜测修复损坏内容。

示意结构如下；示例中的凭据不是有效票据：

```json
{
  "version": 1,
  "installation_id": "ins_example",
  "setup_base_url": "https://gateway.example/api/ai-setup/v1",
  "api_base_url": "https://gateway.example/v1",
  "service_name": "Example AI",
  "app_id": "codex-desktop",
  "credential": {
    "type": "setup_ticket",
    "value": "EXAMPLE_NOT_A_VALID_TICKET"
  }
}
```

以上字段均必需。`installation_id` 为不含秘密的安装记录标识，
只允许字母、数字、下划线和连字符，长度 1–80；不能单独授权访问。
`service_name` 只作初始预览，最多 100 个字符，禁止控制字符和 HTML。
凭据和真实密钥不出现在名称、标识、URL 或错误信息中。

地址规则：

- 两个 base URL 必须为 HTTPS，使用系统证书信任，禁止跳过证书检查。
- 禁止 URL 用户信息、query、fragment、反斜杠和控制字符。
- 主机使用小写 ASCII/IDNA 形式，省略默认 443 端口；非默认端口明确保留。
  路径区分大小写，保留租户路径及 /v1；拒绝点路径段和编码后的路径分隔符。
- `setup_base_url` 必须没有结尾斜杠，完整接口由追加本规范规定的路径构成。
  不从 `api_base_url` 猜测它，也不再发起匿名 well-known 请求。
- `api_base_url` 必须按服务端记录原样绑定；不能在客户端擅自增删 /v1 或结尾斜杠。
  规范化后的两个地址都须在首次握手中与服务端记录比较。
- setup 与 API 可以不同 origin；界面必须显示两者，凭据仅发送给其授权接收方。
  v1 的 `api_key` 兼容模式额外要求两个地址同 origin。
- 认证、配置、凭据及 Logo 请求禁止重定向；服务搬迁须由网站重新签发安装码。

可选字段允许向后兼容扩展，包括类型化对象内的未知可选字段，
但必须仅作为惰性数据忽略；只有适配器白名单中的已知字段可影响配置。
扩展不能改变地址、权限或安装行为，已知字段的非法值仍必须拒绝。
不支持的必需能力应明确拒绝，不能悄悄降级为匿名发现或手动拼配置。

## 4. 网站先分配，再签发

网站沿用自己的登录授权；网站登录 token 不进入安装码。
签发前必须核对分组归属和可用性，选择现有密钥或真实创建专用密钥，
然后原子保存 `installation_id → user / group / key_id / 两个 base URL / app_id / 配置版本`。
网站提供清晰的“使用已有密钥”或“新建安装专用密钥”选择，并可预选一个合理选项。
最终必须是用户已确认的选择，助手不得再分配分组或创建另一把密钥。

点击“复制安装码”才触发签发；单纯浏览页面不创建密钥。
签发操作必须幂等，复制失败或按钮重试不能重复创建专用密钥。
变更组、密钥或 endpoint 后生成新安装记录，并使旧的未认领安装码失效。
已认领的会话单独显示其状态，不能借用新安装码静默改变其授权。

`setup_ticket` 必须是至少 256 位 CSPRNG 随机性的 opaque bearer，服务端只保存其哈希。
客户端不假设任何服务商前缀或固定长度；随机性是签发方要求，不能从票据文本推断。
未认领票据有效期最多 10 分钟；它只能认领指定安装记录，不能调用模型或管理账户。
客户端携带的失效时间（若有）仅用于显示，以服务端时间为准。

`api_key` 是可选兼容模式，仍必须先通过同一 bootstrap 鉴权：
网关须把该 key 与已签发的 installation、用户、分组及地址绑定，
安装记录的首次认领期限同样最多 10 分钟；原 API Key 生命周期不因此缩短。
只实现普通模型 API、没有鉴权 bootstrap 的网关不属于兼容实现。
此模式在剪贴板中暴露长期 key，必须明确告知，不能宣传成短期票据。

## 5. 鉴权握手与安装会话

以下路径均相对于 `setup_base_url`。除 Logo 图片外，请求/响应均使用 application/json。

| 方法与路径 | 授权 | 作用 |
| --- | --- | --- |
| POST /bootstrap | 安装码凭据及本次恢复秘密 | 原子认领并返回配置 |
| GET /sessions/{id} | 安装会话 | 读取本次状态及同一配置快照 |
| GET /sessions/{id}/logo | 安装会话 | 返回受保护的展示图片 |
| POST /sessions/{id}/credentials | 安装会话 | 交付已选 key，或确认使用码中已有 key |
| POST /sessions/{id}/complete | 安装会话 | 幂等记录本地完成回执 |
| POST /sessions/{id}/cancel | 安装会话 | 终止安装会话，不删除客户端或 API Key |

网站签发接口使用服务商已有网页登录体系，不规定通用登录或账户管理接口。
不在 v1 引入 OAuth 服务器、设备指纹或公开客户端内置共享秘密。

首次 bootstrap 前，助手生成随机 UUID `claim_id` 和 32 字节 CSPRNG
`resume_secret`，将其与安装码绑定保存于当前用户受保护的短期恢复记录。
Windows 使用用户级 DPAPI 及私有 ACL，不上传设备指纹、不传入 UAC 子进程。
同一进程或崩溃后的重试必须复用该记录，不能重新生成认领信息。

bootstrap 的 Authorization 为 `Bearer <安装码凭据>`；
`Setup-Session-Proof` 请求头携带无填充 Base64url 编码的 resume_secret，
解码必须恰为 32 字节。后续会话 Bearer 使用相同编码形式。
bootstrap 请求体的字段均必需，示例如下：

```json
{
  "version": 1,
  "installation_id": "ins_example",
  "claim_id": "38d9d2dc-8571-4be8-960b-fef631ed7122",
  "credential_type": "setup_ticket",
  "setup_base_url": "https://gateway.example/api/ai-setup/v1",
  "api_base_url": "https://gateway.example/v1",
  "app_id": "codex-desktop",
  "assistant_version": "1.0.0",
  "architecture": "x64"
}
```

credential_type 只能为 setup_ticket 或 api_key，不能在会话中切换；
architecture 只能为 x64 或 arm64。请求不含网站登录 token。
其余 POST 的请求体为 `{}`，目标和授权均从安装记录取得，不允许覆盖 key 或配置。

服务端先认证，再访问/返回配置，并在事务内处理：

1. 首次认领要求安装记录未过期、未撤销，账户、组、key、应用及两个地址全部匹配。
2. 原子绑定唯一 claim_id 和恢复秘密哈希，设置从认领起最多 2 小时的固定会话期限；
   session id 可使用 installation_id；session_id 与 installation_id 使用相同的
   1–80 位 ASCII 字母、数字、下划线和连字符语法，标识本身没有授权能力。
3. 相同凭据、claim_id 和恢复秘密重试返回同一会话及同一配置，不创建 key、
   不延长期限；其他认领者不能获得任何配置。
4. 原票据过期但已成功认领时，仅原恢复记录可在会话有效期内恢复。
   票据哈希映射须保留到会话终止，不能在首次认领期限到达时提前清除。
5. 明确撤销安装记录、账号、分组或 key 后必须拒绝恢复，不能借幂等绕过撤销。

恢复秘密就是此安装会话的 bearer secret，无需另发行另一枚 session token。
后续请求使用 `Authorization: Bearer <resume_secret>` 并与路径 id 匹配。
公开 claim_id 或幂等请求 ID 都不能单独用于恢复。

所有阶段检查当前授权仍有效；改变既有 key 的组、所属人或 endpoint 绑定后停止，
不得自动改用另一把 key。服务端不按客户端任意 URL 代理请求，只使用已保存的地址。

## 6. 鉴权后的配置

bootstrap 与 GET session 成功响应采用以下结构。
state 反映当前会话，其余配置字段保持同一快照：

```json
{
  "version": 1,
  "session_id": "ins_example",
  "state": "claimed",
  "expires_at": "2026-09-30T12:00:00Z",
  "revision": "config-example-1",
  "setup_base_url": "https://gateway.example/api/ai-setup/v1",
  "api_base_url": "https://gateway.example/v1",
  "app_id": "codex-desktop",
  "service": {
    "name": "Example AI",
    "logo_available": false
  },
  "selection": {
    "group_label": "Default service",
    "key_label": "Desktop",
    "key_hint": "ends in demo"
  },
  "client": {
    "model": "example-model",
    "wire_api": "responses",
    "reasoning_effort": "high"
  },
  "preflight": {
    "status": "passed"
  },
  "mirrors": []
}
```

示例中的日期、模型和标识仅作格式示意。`mirrors` 为可选字段，缺省时规范化为 `[]`；
显式 `null` 或非数组值必须拒绝。除下面注明的可选字段外均必需；
expires_at 是 RFC 3339 UTC 时间，version 为整数，其余标识及文本字段为字符串。
logo_available 为布尔值，mirrors 为数组。v1 wire_api 固定为 responses。
服务及选择标签最多 100 字符，key_hint 最多 32 字符且禁止放完整 key；
model 最多 200 字符，按纯数据处理。reasoning_effort 可选，只接受适配器支持的枚举。
state 允许 claimed、credentials_released、completed；失效/取消会话不返回配置。
bootstrap 和 GET session 返回配置之前必须重新检查当前授权；
preflight.status 只能为 passed，表示绑定权限、指定模型及接口适配的服务端预检通过。
不支持指定模型/接口返回 422 configuration_unsupported，不返回发现内容；
账号或密钥无权使用返回 403，瞬时故障返回 5xx。该预检不是计费推理测试。

| 字段 | 内容与限制 |
| --- | --- |
| version、session_id、expires_at、revision | 协议版本、会话标识、UTC 失效时间及本次配置版本 |
| setup_base_url、api_base_url、app_id | 与安装码和服务端记录一致的已绑定值 |
| service.name、service.website_url、service.help_url | 名称必需，两个无凭据 HTTPS 链接可选 |
| service.logo_available | 是否提供受保护 Logo，不能用远端 Logo URL 提前绕过鉴权 |
| selection.group_label、selection.key_label、selection.key_hint | 用户第一步的选择，仅标签及掩码 |
| client.model、client.wire_api、client.reasoning_effort | 应用适配器允许的类型化配置 |
| mirrors | 可选的已支持应用、架构对应镜像数组；缺省规范化为 `[]`，空数组使用官方源；`null` 或非数组值拒绝 |

首次响应不含最终 API Key。会话期间返回同一快照，不把后台配置变化静默注入正在安装的流程；
撤销权限可以即时生效。若必须修改关键配置，终止会话并提示重新生成安装码。
服务器名称和 Logo 不能覆盖中立助手本身的名称、程序图标或签名发布者。

Logo 只能在鉴权后从规定的同源 session 路径读取，最多 512 KiB，
只接受 PNG/JPEG 且解码尺寸不超过 1024×1024；不接受 SVG、HTML、XAML 或脚本。
服务器无配置 Logo 或加载失败时使用助手默认图标，不阻断安装。
帮助链接只有用户主动点击才打开，不附带密钥、票据或安装会话信息。

类型化客户端配置不能提供任意 TOML/JSON 文本、文件路径、环境变量、命令、
插件或权限开关。v1 Codex adapter 只接受 Responses/API Key 配置及支持的模型参数。
分组权限、模型可用性应在大文件下载前预检；凭据交付及写入前再次检查。
不假定所有网关实现 /models；标准安装响应需表达明确的支持结果，
客户端只调用适配器允许的无计费检查，失败不能一律误报“密钥错误”。
服务器声明不等于真实模型调用已通过，不自动发起有费用的测试请求。

## 7. 领取密钥、完成与失败

credentials 分两种成功响应，均包含 version、session_id、api_base_url，
以及 credential 对象；不得临时新建、轮换或替换 key：

- setup_ticket：credential 为 `{"type":"api_key","delivery":"server","value":"EXAMPLE_NOT_A_REAL_KEY"}`，
  value 是实际已分配 key。此模式要求服务端能安全再次交付同一 key。
- api_key：credential 为 `{"type":"api_key","delivery":"provided_by_client"}`，
  不返回 value；只复核原 key 的绑定及当前授权，助手使用受保护恢复记录中
  已通过 bootstrap 校验的原始 key。服务器无需保存或还原 key 原文。

助手根据原安装码 credential_type 严格检查 delivery，禁止服务器切换模式。
仅存不可逆 key 哈希的系统不能托管交付其无法取回的旧 key。
只有网页或用户本来就持有 key 原文，才能生成 api_key 安装码；
该兼容模式不是找回丢失密钥的方法。

返回前原子记录 `credentials_released_at`，含义是“已交付或可能已交付”。
会话有效且授权未撤销时，重试返回同一交付结果，不依赖第一次响应是否成功到达。
长期 key 存储复用网关既有安全机制，不在安装记录、队列或日志额外复制明文。
兼容模式的 API Key 已由用户交付给助手，不能按“从未暴露”处理。

客户端在包验证及用户确认后交付或确认凭据，保守合并配置、备份并检测外部修改，
然后发送 complete；已安装兼容客户端时跳过下载安装。
用户凭据与配置始终属于启动助手的原用户，UAC helper 只处理无凭据安装工作。
应用的用户级检测、注册及启动也在原用户上下文进行；helper 只处理必要的机器级步骤，
不得因跨账户 UAC 将 MSIX 用户注册或启动放到授权管理员账户。
系统要求注销/重启才能完成注册时明确提示，不伪装成当前用户已经可启动。

complete 是幂等回执，不是跨服务器与本地文件系统的原子提交。
回执丢失不能导致密钥失效；已完成会话可在原期限内返回原结果，便于恢复。
complete 成功响应为 version、session_id、state=completed；
cancel 成功响应同形但 state=canceled。没有成功交付/确认凭据的会话不能 complete。
cancel 为终态，后续除原秘密授权的 cancel 幂等回执外均拒绝；
completed 后的 cancel 仅终止恢复会话，不撤销 key 或改变本地完成事实。
cancel 或会话超时停止后续交付，但 v1 不自动撤销任何已分配 API Key，
也不卸载客户端或删除分组。网站列出本次专用 key，允许用户显式撤销；
避免“助手尚未领取”但用户已从其他页面取得 key 时发生误撤销。

收到 complete 成功响应后删除恢复记录；本地已成功但回执响应丢失时，
保留至重试成功或原会话期限到达，不重新安装。用户可显式结束恢复并删除记录。
明确退出、取消或过期也删除记录；异常退出只保留到会话最大期限。
服务器的 completed 标记不能提前删除有效期内的哈希映射。
确认成功及过期后不继续保存票据或恢复秘密，长期 key 只留在目标客户端要求的私有配置中。
API Key 不传给 Logo、帮助网站、包镜像、遥测或管理员安装进程。
粘贴后仅在剪贴板仍等于本次安装码时清理；不声称能清除系统历史或云剪贴板。

## 8. 安装包与镜像

应用适配器定义官方来源、允许的包身份与发布者、架构、版本下限及安装方式。
`mirrors` 缺省或为 `[]` 时走该适配器的官方源；
缺省必须规范化为 `[]`，显式 `null` 或非数组值拒绝。
不存在该架构官方包时明确提示不支持。
合法服务商配置不能改变上述信任策略。

v1 mirrors 数组每项固定包含 app_id、architecture、version、package；
license 仅在适配器要求许可证时必需。app_id 必须与当前安装一致，
architecture 为 x64/arm64，相同架构不得重复；version 是官方包版本字符串。
package 与 license 均为包含 url、bytes、sha256 的对象：
url 为无凭据 HTTPS URL，bytes 为正整数，sha256 为 64 位小写十六进制摘要。
该数组只提供完整官方包及许可证，不接受任意 EXE 命令或安装参数。
需要许可证的包必须完整配对，不混用不匹配来源或版本。
服务端给出的哈希只证明传输与其清单一致，不能独立证明官方身份。

执行前验证签名信任链、预期官方发布者与应用身份、架构和版本规则，
拒绝降级已安装的更新版本；当前 Codex 适配器使用官方 MSIX 安装流程。
提权后继续使用私有路径及安装前校验，不能允许校验后被低权限进程替换。
证书轮换由经过审查的适配器更新管理，不接受远端配置追加可信发布者或根证书。

包和许可证使用独立无凭据 HTTP 客户端，绝不继承 Authorization、Cookie 或恢复秘密。
官方源按适配器审查过的重定向规则下载；自定义镜像 v1 不跟随重定向。
镜像校验失败禁止执行，用户可选择官方源重新下载和验证，不能静默放宽检查。
本协议仅限制助手在鉴权前发现或使用服务配置，不要求原本公开的官方安装包改成私有。
分块镜像等扩展须单独定义和验证，v1 不把未知扩展当作可执行能力。

## 9. 响应保护与错误

bootstrap 及所有 session 响应（含错误和 Logo）必须设置 Cache-Control: no-store，
CDN/反向代理必须绕过缓存；不把凭据放 query、路径、重定向或错误回显。
日志、追踪、错误上报和网关观测必须屏蔽 Authorization、Setup-Session-Proof、
安装码、恢复记录及 credentials 响应，不能依赖通用日志自动脱敏。
仅校验客户端 UI 或只设置缓存头，均不代替服务器鉴权。

| HTTP 状态 | 语义 | 用户处理 |
| --- | --- | --- |
| 400 | 请求格式、版本或绑定字段不正确 | 重新复制，必要时更新助手 |
| 401 | 无凭据、凭据无效/过期或会话不可用 | 回网站重新生成安装码 |
| 403 | 已认证但当前权限、组或 key 不允许 | 网站检查已选服务，不擅自换 key |
| 409 | 有效凭据对应的安装记录已由不同会话认领 | 使用原会话恢复或网站重新签发 |
| 422 | 已鉴权但配置与应用适配器不兼容 | 网站调整选择或更新助手 |
| 429 | 请求频率受限 | 按 Retry-After 有限重试 |
| 5xx / 网络异常 | 暂时失败 | 保留有效恢复记录，有限退避重试 |

在返回绑定字段、认领冲突等信息前先验证凭据，避免通过 installation_id 枚举用户状态。
失败只返回中立错误码及安全请求编号，不返回品牌、Logo、镜像、分组或 key 信息。
错误结构固定为 `{"error":{"code":"invalid_credential","request_id":"req_example"}}`。
对应状态的 code 分别使用 invalid_request、invalid_credential、access_denied、
claim_conflict、configuration_unsupported、rate_limited、temporarily_unavailable；
不得把错误内容直接作为可执行操作或包含远端 HTML 的提示。
所有 AGSP JSON 响应（包括配置、credentials、回执及错误）上限 128 KiB；
bootstrap 请求体上限 16 KiB，其他 session POST 请求体严格为 `{}`。
上述上限按 UTF-8 消息体字节数检查；图片和安装包另受各自大小、超时及取消策略约束。
不支持的主版本或认证失败必须停止，不能退回匿名发现。

## 10. 最低互操作验收

- 安装码包含明确服务入口、完整 API 地址及服务名称；改文件名不影响行为。
- 匿名、损坏、未认领票据过期、会话过期、撤销、绑定不符和陌生认领均无法取得配置或 Logo。
- setup 与 API 的路径、端口、大小写规则一致，凭据不经重定向泄露。
- 先选定 group 并创建/选择 key，再发码；双击复制、网络重试不额外创建 key。
- bootstrap 已提交但响应丢失后，原恢复记录可恢复；仅 claim_id 无法恢复。
- 已认领票据超过首次 10 分钟期限但会话仍有效时，只有原恢复记录可恢复。
- 票据首次期限、会话期限、撤销和过期后的拒绝均由服务器强制执行。
- Logo 缺失可继续，配置鉴权失败不可继续；没有镜像时使用官方源。
- 镜像哈希、签名、产品身份或架构不符时不执行；不接受服务商放宽策略。
- credentials 重试返回同一交付结果；api_key 模式不要求服务端还原 key；
  complete 响应丢失可恢复，完成及取消不自动撤销 key。
- 现有客户端、不同 UAC 账户、备份恢复、外部并发修改及两架构均验证。
- 无凭据进入日志、公开构建物、浏览器持久化、下载源请求或管理员进程。

## 11. 标准依据

- [RFC 6750](https://www.rfc-editor.org/rfc/rfc6750.html)：bearer 凭据传输和泄露风险。
- [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html)：限制凭据用途及避免 URL 暴露。
- [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252.html)：未来若支持浏览器授权，采用原生应用标准流程；本协议票据不称为 OAuth/PKCE。
- [Windows 签名校验](https://learn.microsoft.com/en-us/windows/win32/seccrypto/example-c-program--verifying-the-signature-of-a-pe-file)及
  [包身份](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)：安装器身份验证基础。

## 12. Frozen representation and validation rules

These rules complete the implementation contract alongside the JSON Schema and
vectors. JSON Schema validates structure only; semantic and HTTP checks are mandatory.

- KiB means 1024 bytes. Check the trimmed installation code before decoding
  (16,384 ASCII bytes), then strictly decode unpadded canonical Base64url and UTF-8
  (at most 8,192 bytes). Do not replace invalid UTF-8. Reject duplicate property names
  at every JSON depth, including names made equal by JSON escape decoding. The same
  strict JSON decoder is required for HTTP JSON bodies. Only surrounding whitespace
  may be removed from the complete installation-code text.
- Tickets are opaque RFC 6750 bearer tokens with at least 256 bits of issuer-generated
  randomness. Do not require a private prefix, try to decode their contents, or infer
  their entropy from a prefix/length. A syntactically valid ticket is not authenticated.
  API keys use bearer token syntax `[A-Za-z0-9._~+/-]+` followed by zero or more `=`
  padding characters. In particular `+`, `/`, `=`, and `~` are allowed; whitespace,
  controls, CR/LF, and header injection are forbidden. No independent key-length
  limit is invented: code and JSON-body limits apply to the whole message.
- Proofs and session bearers are canonical unpadded Base64url of exactly 32 bytes
  (43 characters, zero unused trailing bits). Check decode and exact re-encoding;
  a length check alone is insufficient. Header names are case insensitive; duplicate
  Authorization or proof headers must reject. Bootstrap requires the code bearer
  and Setup-Session-Proof; later requests use only the proof as their bearer.
- Parse URLs strictly before normalizing. Reject userinfo, backslashes, controls,
  invalid percent escapes, dot path segments (including percent-encoded dots),
  and encoded `/` or `\` path separators. Base URLs also reject query and fragment.
  Lowercase HTTPS and the ASCII/IDNA host, omit port 443, preserve other ports,
  path case, tenant prefixes, valid path escapes, and API trailing slash exactly.
  Do not resolve dot paths, collapse slashes, append `/v1`, or repair URLs. The
  setup base must not end in `/`. Missing authority, invalid ports and URI syntax
  must reject. A URI parser's silent repairs are not canonicalization.
  Asset/link URLs may carry non-secret query/fragment data, but never credentials;
  assets are downloaded only under the adapter's trust and redirect policy.
- Compare normalized setup and API URLs with the saved binding. Origin means
  normalized scheme, host, and effective port. API-key mode requires equal origins;
  ticket mode can bind different origins. Neither grants permission to forward a
  credential anywhere else. All setup requests forbid redirects.
- `claim_id` is a CSPRNG UUIDv4. `session_id` uses the same 1–80 character
  path-safe ASCII identifier grammar as `installation_id`. `revision` and
  `assistant_version` are nonempty control-free strings, bounded by their enclosing
  message. `reasoning_effort` is a string at schema level and must be validated
  against the local adapter's supported values; there is no global AGSP enum.
- All JSON responses are at most 131,072 UTF-8 body bytes. Bootstrap JSON requests
  are at most 16,384 body bytes. Other session POSTs are exactly an empty JSON object
  with no fields. Reject oversized input before using any fields. Lengths on labels
  and models count Unicode code points, not UTF-8 bytes or UTF-16 code units.
- `expires_at` is a valid RFC 3339 UTC timestamp (`Z` or `+00:00`); schema format
  assertions must be enabled. Expiry, fixed deadlines, authorization, snapshot
  equality, mirror uniqueness by architecture, mirror app binding, package/license
  pairing, supported reasoning, safe text, and key-hint masking require semantic
  checks. Never use the schema as evidence of authorization or package trust.
- Unknown optional fields are inert and ignored, including inside typed objects.
  They cannot install a command/plugin, supply raw configuration, alter trust,
  or override binding. Invalid values for known fields always reject. Fields
  explicitly forbidden by a delivery mode (`value` for `provided_by_client`) still
  reject; errors keep their fixed shape and never carry discovery data. A client
  must reject unsupported required capabilities, not reinterpret them as optional.
- `mirrors` is optional: normalize omission to `[]` before canonical comparison;
  both select the official source. Explicit `null` and wrong-type values reject.
  This default is a semantic rule, not a JSON Schema validator side effect.
- Endpoint schemas are selected from `$defs`, not inferred from the root union.
  Success responses are HTTP 200. Error status/code mappings are fixed in section 9;
  `temporarily_unavailable` may use a 5xx status. Every setup response, including
  errors and images, carries `Cache-Control: no-store`. Transport failures have
  no synthetic JSON response.

No schema or fixture proves cryptographic randomness, live revocation, transaction
atomicity, trusted package identity, or real installation success. Both independent
implementations must run these vectors and their own stateful/security tests.
