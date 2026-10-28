# Security

Mindtalk runs entirely on your Mac: nothing you say, no text and no voiceprint is ever sent anywhere. Dictation audio is never written to disk; meetings you record are kept in your own Library folder. The only network traffic is downloading the models you use and checking for updates.

## Reporting a vulnerability

Please report security issues **privately** through GitHub: open the [Security tab](https://github.com/dragon6sic6/Mindtalk/security) and choose **Report a vulnerability**. Don't open a public issue.

Include what you found, how to reproduce it, and the Mindtalk and macOS versions. You'll get an answer within a few working days.

## How releases are protected

- Every release is signed with Mindact Solutions AB's Developer ID and notarized by Apple.
- Updates (via [Sparkle](https://sparkle-project.org)) are signed with Mindtalk's own EdDSA key, the update feed is signed too, and each update is verified before it's unpacked.
- Speech models (and Gemma, if you choose it) are downloaded from pinned revisions on Hugging Face and every file is checked against its SHA-256 before use. The meeting and word-list models (Nemotron 3, Parakeet CTC 110M) are fetched by FluidAudio from FluidInference's repositories on Hugging Face.
