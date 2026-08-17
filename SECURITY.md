# Security Policy / 安全策略

## Reporting a vulnerability / 报告安全问题

Please do not publish exploitable details, API keys, private documents, or user data in a public issue. Use GitHub's private vulnerability reporting feature when it is enabled. If private reporting is not yet available, open a minimal issue asking the maintainer for a private contact channel without including technical details.

请勿在公开 issue 中发布可利用细节、API Key、私人文档或用户数据。优先使用 GitHub Private Vulnerability Reporting；若尚未启用，请只提交一个不含技术细节的简短 issue，请维护者提供私密联系方式。

Include the affected version, impact, reproduction conditions, and a minimal proof of concept that does not contain real credentials or documents. You should receive an acknowledgement within seven days.

请提供受影响版本、影响范围、复现条件和不包含真实凭据/文档的最小证明。维护者目标是在七天内确认收到。

## Supported versions / 支持版本

Security fixes are provided for the latest published release. Older builds may be asked to upgrade before a report is investigated.

安全修复以最新正式版为主。旧版本用户可能需要先升级再继续排查。

## User security notes / 用户安全提示

- API keys belong in Windows Credential Manager and must never be committed.
- Verify release SHA-256 values before running unsigned community builds.
- Online mode sends document content to the endpoint selected by the user; local mode keeps model inference on the device.
- Sanitize `%LOCALAPPDATA%\TransReader\logs` before sharing diagnostics.
