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
