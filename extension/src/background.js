// PRIVON MV3 extension -- production service-worker entry point. Zero logic of its own beyond
// wiring the real chrome.* global into PrivonSession (session.js); every actual mechanical decision
// lives in session.js/stateMachine.js/protocol.js/origin.js, which are testable in isolation from
// any real browser (see extension/test/).

import { PrivonSession } from "./session.js";

const session = new PrivonSession({ chromeApi: chrome });
session.start();
