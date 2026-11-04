# Threat Model

## OPEN_QUESTION: SEND_INTERCEPTION_CAPABILITY

감사 계약(✅ PRIVON 0.1 최종 감사 · 구현 계약) 3.1의 PHASE0_BLOCKER 조건에 따라 Phase 0에서 실측 검증한 결과, direct typing 경로(키보드로 입력 후 Enter 또는 마우스로 Send 버튼 클릭) 전체를 전역 키보드 후킹·DLL/프로세스 인젝션·네트워크 가로채기 없이 신뢰성 있게 차단하는 방법을 찾지 못했다.

- Enter 단독 전송: `RegisterHotKey` 기반으로 차단 가능함을 확인 (PASS).
- 마우스로 Send 버튼 클릭: transparent overlay 방식을 반복 실측했으나 차단에 계속 실패함 (FAIL).

두 경로 중 하나가 막히지 않으므로, direct typing 전체 경로는 0.1에서 완전 보호 대상으로 주장할 수 없다. 이 OPEN_QUESTION은 닫지 않고 남겨둔다.

## 0.1이 보호하는 것
- Clipboard를 경유하는 전송 경로: 사용자가 원문을 복사하면 PRIVON이 로컬에서 보호본으로 치환하고, 사용자가 직접 붙여넣는다.

## 0.1이 보호하지 않는 것
- Composer에 직접 타이핑된 개인정보가 Enter 또는 마우스 Send 클릭으로 전송되는 경로.
- 이 경로에 대해서는 "보호됨" 표시를 하지 않는다.

## 정정 및 보충 (0.4.0) — 위 기록은 삭제하지 않고 보충한다

위 `SEND_INTERCEPTION_CAPABILITY` 기록은 Phase 0 중간 결과이다. 이후 실측(https://app.notion.com/p/3bcfce3c4fde81bcba35f186a18ddc0f)에서 다음이 추가로 확인되었다. 이 보충은 OPEN_QUESTION을 닫지 않는다.

- **"transparent overlay 차단 실패(FAIL)"의 범위**: 반복 실패한 것은 WPF overlay이다(실제 마우스 Send 차단 4/4 실패, 폐기 후보). 이후 시험한 Native Win32 HWND shield(`WS_POPUP`, layered/topmost/toolwindow/noactivate)는 고정 위치 10/10 차단, 동적 추적 PASS로, **대상이 정상 식별되는 동안에 한해** 조건부로 성공했다.
- **Combined Stress(약 400초)의 실제 전송 누출**: Enter 159/159 차단(실제 전송 0), Shift+Enter 10회 이상 정상. 그러나 Mouse Send는 약 83회 중 81건 차단, **2건 실제 전송 누출**. UIA Send 버튼 탐색은 305회 중 53회 실패, 탐색 공백 3회(3681 / 2456 / 2597 ms, 총 8734 ms, 최장 약 3.7초). 이 공백 동안 스파이크는 shield를 숨겼다(fail-open).
- **직접 쓰기**: `ValuePattern.SetValue()`는 예외 없이 성공처럼 보이나 실제로는 아무것도 바꾸지 않는 no-op이다. `SendInput` 기반 직접 쓰기는 개행/Enter 부작용 때문에 채택하지 않았다.
- **검증 범위의 한계**: 단일 앱(ChatGPT Windows)·단일 환경에서의 실측이며, 클립보드 지연은 1회 표본이다. 위 기록의 검증 상태는 `unverified`이다. 따라서 **direct typing 전체의 fail-closed 보장은 증명되지 않았다**.

### 실험 코드와 출고 제품 코드의 차이

- Enter 핫키(`RegisterHotKey`)와 Native shield는 `tools/UiaInspector`(Phase 0 스파이크)에만 있다. 이 도구는 출고 제품이 아니며, 출고 어셈블리(`src/`)에는 전송을 가로채거나 막는 코드가 없다(키보드/마우스 후킹, 핫키, overlay, `SendInput` 없음).
- 출고 제품의 UI Automation 사용은 `FocusedElement`를 호출 시점에 읽는 **읽기 전용**이다. 컴포저에 쓰지 않는다.
- 스파이크 코드는 결과 보존을 위해 변경하지 않는다.

### 상태 표시의 차이

- **출고 App**: 보호 상태 트래커가 있고(Controller 승인으로 출고 기본 App에 연결), 트레이 툴팁이 마지막 클립보드 처리의 상태(검사 중 / 사용자 결정 대기 / 마지막 처리의 보호본 재작성 확인됨 / 보호 확인 안 됨)를 보여 준다. 실패(Limited, Error)가 2초 이상 이어지면 경고 알림을 한 번 띄운다. 이 표시는 전송을 차단하지 않으며 전송 안전을 보증하지 않는다. 지원하지 않는 앱에서 복사한 내용(또는 텍스트가 아닌 내용)은 검사하지 않으며, 툴팁은 "검사 끝"이 아니라 "지원하지 않는 앱에서 복사됨, 검사 안 함"이라고 표시한다. 아무것도 복사하지 않은 시작 직후에는 "아직 검사한 내용 없음"이라고 표시한다. "검사하지 않음"과 "차단하지 않음"은 서로 다른 사실이다. 사용자가 항목을 원문 그대로 두기로(BypassOnce) 선택하면 툴팁은 "원문 유지를 선택함, 해당 항목은 보호되지 않음"이라고 표시한다. 이는 사용자의 선택(그리고 일회 허용 발급)만을 말하며, 허용이 실제 평가에서 소비되었다는 뜻이 아니다(제품에는 그 평가를 호출하는 곳이 없다). 완료, 보호됨, 검증됨으로 표현하지 않는다.
- **출고 App의 미국 개인정보 탐지(명시적 opt-in, 0.4.0 P1)**: 미국 탐지기 13종의 구현은 `src/Privon.UsProductIntegration`(와 두 코어 `Privon.UsSsn`, `Privon.UsMbiSevis`)에 있고 출고 App이 이를 참조한다. 그러나 **사용자가 Settings에서 직접 켜기 전에는 미국 탐지기가 실행되지 않는다.** 선택 전에는 기존과 똑같은 한국·Global 파이프라인(`DetectionPipeline.CreateDefault()`)만 동작하며 미국 탐지기는 생성조차 되지 않는다. "선택하지 않음"은 인증된 OFF 선택이 아니며, 국가 설정 값으로 바꾸어 표현하지 않는다. 사용자가 켜면 국가 선택(On)과 별도 활성화 기록(`country-activation.bin`, 같은 DPAPI 보호 키·AES-GCM·원자적 교체)이 차례로 기록되고 각각 다시 읽어 확인된다. 활성화 이후의 OFF·손상·인증 실패는 기존 계약(인증된 Off만 팩을 멈추고 나머지는 활성 유지)을 그대로 따른다. 활성화 기록 자체가 손상·인증 실패·더 새로운 버전이면 미국 탐지는 켜지지 않고 "확인할 수 없음"으로 표시하며, 정상적인 OFF 선택으로 표시하지 않는다. 운영 공식 자료(Authority)가 완전히 검증되지 않으면 미국 탐지는 제공·표시되지 않는다. 이 기록들은 같은 Windows 사용자 계정의 무결성·범위를 지킬 뿐, 사람의 의사를 암호학적으로 증명하지 않는다. 트레이의 "준비" 표시는 탐지기를 실행할 수 있다는 뜻이지 보호나 전송 안전이 아니다.
- **실험용 opt-in Host**(`tools/Privon.UsOptInHost`; 출고에 포함되지 않음): 출고 App과 같은 `src/` 탐지기 구현을 쓰되, 별도 활성화 기록 없이 실행 플래그(`--experimental-us-opt-in`)만으로 미국 탐지가 켜지는 실험 구성이다. 출고 패키징은 이름에 `Experiment` 또는 `OptInHost`가 들어간 산출물을 거부한다.

### Clipboard `Verified`의 의미

`Verified`는 PRIVON이 클립보드 텍스트를 재작성하고 다시 읽어 확인했다는 뜻이다. 붙여넣기나 전송이 차단되었다는 뜻도, 안전하다는 뜻도 아니다. 이 제품은 붙여넣기와 전송을 차단하지 못한다.

### 후속

전송 통제가 가능한지는 [Send Guard Proof Contract](send-guard-proof-contract.md)의 합격 조건(G1~G4)과 [측정 프로토콜](send-guard-measurement-protocol.md)로 별도로 검증한다. 그 결과가 나오기 전까지 이 문서의 "0.1이 보호하지 않는 것"은 그대로 유효하다.
