# Security

Do not open a public issue containing Google OAuth credentials, access tokens,
spreadsheet IDs, personnel information, or private forum content.

For security vulnerabilities, use GitHub's private vulnerability reporting for
the repository. Revoke and replace any credential that has been posted publicly;
deleting it from a commit is not sufficient once it has been pushed.

Release executables are published by GitHub Actions with a matching SHA-256
checksum. The in-app updater refuses downloads that do not match that checksum.
