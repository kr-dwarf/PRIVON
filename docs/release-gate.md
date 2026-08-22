# Release Gate

## Phase 0 결과에 따른 범위 조정

Phase 0 기술 스파이크 결과에 따라, 0.1의 Release Gate는 **Clipboard-first protection 경로**를 기준으로 적용한다. Direct typing(키보드+마우스 Send) 전체 경로에 대한 "보호 성공" 게이트는 0.1에 적용하지 않는다 — 이 경로는 애초에 보호됨으로 출시되지 않기 때문이다 (threat-model.md의 OPEN_QUESTION: SEND_INTERCEPTION_CAPABILITY 참고).

기존 감사 계약(✅ PRIVON 0.1 최종 감사 · 구현 계약) 15장의 Release Gate 조건들은 그대로 유효하며, 이 문서는 그 중 clipboard 경로에 해당하지 않는 항목의 적용 범위만 명확히 한다.

## 0.1 Release Gate 대상
- Clipboard 감지 → 치환 → read-back 검증 → 사용자 붙여넣기까지의 전체 흐름
- Clipboard 경로 기준의 Level 1~3 탐지 정확도
- 로그/telemetry에 clipboard 원문 미노출
- ChatGPT가 아닌 다른 앱 사용 중 clipboard 처리가 잘못 개입하지 않는지

## 0.1 Release Gate 비대상 (명시적 제외)
- Direct typing 후 Enter 또는 마우스 Send로 전송되는 경로의 "보호 성공" 검증 — 이 경로는 보호 대상이 아니므로 게이트가 성립하지 않는다.
