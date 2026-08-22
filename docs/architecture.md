# Architecture

## Canonical Protection Path (0.1)

PRIVON 0.1의 canonical protection path는 **Clipboard-first protection**으로 확정한다. 이 결정은 Phase 0 기술 스파이크(`tools/UiaInspector`, 실행 중인 ChatGPT Windows 앱 대상 실측)의 결과에 근거한다.

### 검증된 경로 (PASS)
- ChatGPT Windows 앱 식별 (일반 사용자 권한, UI Automation)
- composer 탐색 및 텍스트 읽기
- Clipboard 변경의 event-driven 감지 (`AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`)
- Clipboard 원문 → 보호본 치환 (`GetClipboardSequenceNumber` 기반으로 처리 도중 사용자의 재변경을 감지해 덮어쓰지 않음)
- 치환 직후 read-back 검증
- 사용자가 직접 Ctrl+V 했을 때 보호본만 composer에 존재함을 확인
- 한글 / 여러 줄 / 이모지 보존
- `RegisterHotKey(VK_RETURN)` 기반 Enter interception — 전용 owner thread에서만 Register/UnregisterHotKey를 호출하는 구조로 lifecycle 문제(다른 앱 Enter를 삼키는 leak)를 해소하고, 정상 종료 시 hotkey가 확실히 해제됨을 실측으로 확인

### 채택하지 않는 경로 (FAIL)
- UI Automation을 통한 composer 직접 쓰기 (`ValuePattern.SetValue`, 포커스+합성 입력 등 시도했으나 이 앱에서 신뢰성 있게 동작하지 않음)
- Transparent overlay 기반 Send 버튼 click shield (버튼 위치 추적 자체는 정확했으나, 클릭 차단이 반복 실측에서 계속 실패함)

### Enter interception을 핵심 경로에 결합하지 않는 이유
Enter 단독 전송 차단은 기술적으로 PASS했지만, 마우스로 Send 버튼을 클릭하는 경로는 차단할 수 없다(위 FAIL 참고). 두 경로 중 하나라도 뚫리면 "direct typing 전체를 보호한다"고 주장할 수 없으므로, Enter interception을 0.1의 핵심 보호 경로에 억지로 결합하지 않는다. 구현 자체(owner-thread 기반 register/unregister lifecycle)는 향후 재검토를 위한 기술 자산으로 보존한다.

## 0.1 범위에서 제외되는 것
- Composer에 대한 PRIVON의 직접 쓰기(작성)
- 마우스 Send 클릭을 포함한 direct typing 경로의 완전 차단
