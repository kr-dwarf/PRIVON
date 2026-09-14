# PRIVON Chrome Web Store 0.3.2 provenance receipt

Verification context: 2026-09-14, Gate 032-FR. Chrome Web Store state was not changed.
The frozen commander record identifies this exact archive as the 0.3.2 package submitted
for review with auto-publish disabled.

## Submitted package identity

- Filename: `PRIVON-Chrome-WebStore-0.3.2.zip`
- Original retained artifact: `C:\Users\user\Desktop\PRIVON-032-STORE-PACKAGE-CANDIDATE\PRIVON-Chrome-WebStore-0.3.2.zip`
- Size: `28,883` bytes
- SHA-256: `9da02a3b4b3f38533b0de37864d670a4978ff6e79405b1f59036af1694aa52b8`
- Manifest version: `0.3.2`
- Entries: exactly 10; `manifest.json`, five runtime JavaScript files, and four PRIVON icons

## Source mapping

The independent Gate 032-B2E1 package audit read every uncompressed ZIP payload and the
corresponding Git object at source commit
`309863f8a339b533526bb804379ffd36f6957248` (tree
`98ff233684559dcb84917b0997f0d3d74e50bbba`). All 10 payloads were byte-identical to their
Git objects. An independent deterministic reconstruction also produced the same 28,883
bytes and SHA-256. Its verdict was:

`032B2E1_DETERMINISTIC_STORE_PACKAGE_INDEPENDENT_AUDIT_PASS_FREEZE_READY`

The frozen 0.3.2 source candidate reviewed by Gate 032-FR is:

- HEAD: `49f3da198853917f26f1dc4a4061a7422aa07dfb`
- Tree: `7fea126a41b77c42b3c269bee55eae70db4d4a62`

`git diff 309863f8a339b533526bb804379ffd36f6957248 49f3da198853917f26f1dc4a4061a7422aa07dfb -- extension`
is empty, and both commits reference the same Git blob IDs for every packaged extension
file. The submitted package therefore maps byte-for-byte to the extension source in the
frozen candidate. Later candidate commits changed desktop branding/onboarding only, not
the submitted extension payload.

## Package payload SHA-256

| Entry | SHA-256 |
|---|---|
| `manifest.json` | `60e292db63d11e63eb73fd8ce1b52bd28587592ed9977af89f9a3399e69326ef` |
| `src/background.js` | `5309093c4cd98e30ba76e8f844fdcf4e0e384813a7f039c11b1c023baa03a961` |
| `src/origin.js` | `b763c2edd44f091ac250a0fb3feee7bfa7c23c3193607620d274bbf06c2e0d0d` |
| `src/protocol.js` | `100de2c11b4f437f187ab1d8c5f3f1a358c32780d90afe500ff75faa3fbdee52` |
| `src/session.js` | `aa675367599e59c5fa8d5034aadac939c5d97f48319f8589091bc7cb054a2ff6` |
| `src/stateMachine.js` | `c3d80963e003936f5a23b8e2f90f57be8d26ac34c76abc1b258f53e54fde75d0` |
| `icons/privon-16.png` | `e2e3672123bce94877381b9972021bc558c04a37864b48f82f99c44a73430231` |
| `icons/privon-32.png` | `fbb25abbbf9855d93dc3d497250b7f2ff9103bdc5f5fd1c1f40caee840780589` |
| `icons/privon-48.png` | `e3100a61ca907bdec5a632d1c8613e179da5f0bd6b7e821f1b17bbbb0389d79c` |
| `icons/privon-128.png` | `0ca845c5b89011e277ca9731123152635194573db90b6ca41297a7cf2075c985` |

This receipt records existing submitted-byte evidence. It is not a regenerated package,
publication record, runtime cryptographic guarantee, or claim that Store review has
completed.
