# Security

Mindtalk runs entirely on your Mac: audio is never written to disk or sent anywhere, and the only network traffic is downloading a speech model you chose and checking for updates.

## Reporting a vulnerability

Please report security issues **privately** through GitHub: open the [Security tab](https://github.com/dragon6sic6/Mindtalk/security) and choose **Report a vulnerability**. Don't open a public issue.

Include what you found, how to reproduce it, and the Mindtalk and macOS versions. You'll get an answer within a few working days.

## How releases are protected

- Every release is signed with Mindact Solutions AB's Developer ID and notarized by Apple.
- Updates (via [Sparkle](https://sparkle-project.org)) are signed with Mindtalk's own EdDSA key, the update feed is signed too, and each update is verified before it's unpacked.
- Speech models are downloaded from pinned revisions on Hugging Face and every file is checked against its SHA-256 before use.
