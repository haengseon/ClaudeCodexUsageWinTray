<h1 align="center">
  <img src="assets/banner.png" alt="ClaudeCodexUsageWinTray" width="100%">
</h1>

<p align="center">
  <a href="README.md"><img src="https://img.shields.io/badge/English-6e7781?style=flat-square" alt="English"></a>
  <a href="README.ko.md"><img src="https://img.shields.io/badge/%ED%95%9C%EA%B5%AD%EC%96%B4-0969da?style=flat-square" alt="한국어"></a>
</p>

<p align="center">
  Claude와 Codex 사용 한도를 Windows 작업 표시줄에서 바로 확인하세요.
</p>

<p align="center">
  <a href="https://github.com/haengseon/ClaudeCodexUsageWinTray/releases/latest"><img src="https://img.shields.io/github/v/release/haengseon/ClaudeCodexUsageWinTray?style=flat-square" alt="Release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?style=flat-square" alt="Platform">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/haengseon/ClaudeCodexUsageWinTray?style=flat-square" alt="License"></a>
</p>

5시간 한도가 얼마나 남았는지 보려고 `/usage`를 계속 치다가 만들었습니다. 그 숫자를 작업 표시줄 시계 옆에 항상 띄워 둡니다.

## 이렇게 보입니다

```
  42    17     ← 지금 5시간 세션 (사용한 %)
 ▬▬──  ▬───
  18    64     ← 주간 한도 (사용한 %)
 ▬───  ▬▬▬─
Claude Codex
```

- 알림 영역 바로 왼쪽에 작은 숫자 칸이 생깁니다. 주황색이 Claude, 초록색이 Codex예요. 로그인한 것만 칸이 생기고, 나중에 다른 쪽에 로그인하면 알아서 칸이 늘어납니다.
- 클릭하면 `/usage`에서 보던 내용이 나옵니다. 한도별 사용량, 리셋 시각, 리셋까지 남은 시간까지요. 아래쪽 새로고침 버튼을 누르면 바로 다시 확인합니다.
- 기본으로 5분마다 확인합니다. 우클릭하면 갱신 간격(1~60분)을 바꾸거나, 로그인 상태 확인, Windows 시작 시 자동 실행, 자동·수동 업데이트 선택, 종료를 할 수 있어요.
- 사용량이 80%를 넘으면 게이지가 빨간색으로 바뀝니다.
- 메뉴와 문구는 Windows 표시 언어(한국어/영어)를, 날짜와 시간은 지역 설정 형식을 따릅니다.

따로 설정할 건 없습니다. Claude Code와 Codex가 PC에 저장해 둔 로그인 정보를 그대로 읽고, 사용량 조회 API만 부릅니다. 프롬프트를 보내지 않으니 확인한다고 한도가 줄지 않아요.

## 설치

Windows 10 또는 11이면 됩니다. 명령줄 도구든 데스크톱 앱이든 상관없어서 Claude Code CLI나 Claude 앱, Codex CLI나 Codex 앱 중 하나만 있어도 됩니다. 아직 아무것도 로그인하지 않았어도 실행되고, 로그인하는 방법을 안내해 줍니다.

1. [Releases](https://github.com/haengseon/ClaudeCodexUsageWinTray/releases/latest)에서 `ClaudeCodexUsageWinTray.exe`를 받습니다.
2. 아무 폴더에나 두고 실행하면 됩니다. 설치 과정은 없어요.
3. 몇 초 뒤 시계 옆에 숫자가 나타납니다.

부팅할 때마다 켜지게 하려면 숫자를 우클릭해서 **Windows 시작 시 자동 실행**을 켜세요. 새 버전이 나오면 알아서 업데이트됩니다. 직접 정하고 싶다면 **업데이트** 메뉴에서 **자동 업데이트**를 끄고, 원할 때 **지금 확인**을 누르면 됩니다.

## 무엇에 접근하나요

일반 사용자 권한으로 실행되고, 관리자 권한은 필요 없습니다. 사용 통계도 보내지 않고, 아래 주소 말고는 어디에도 접속하지 않아요.

| 대상 | 언제, 왜 |
| --- | --- |
| `%USERPROFILE%\.claude\.credentials.json` | Claude 로그인 정보를 읽습니다. 토큰이 만료됐는데 Claude가 아직 갱신하지 않았으면, Claude Code와 같은 방식으로 갱신해서 같은 파일에 다시 저장합니다. |
| `%USERPROFILE%\.codex\auth.json` | Codex도 마찬가지입니다. |
| `api.anthropic.com`, `platform.claude.com`, `console.anthropic.com` | Claude 사용량 조회(기본 5분마다), 토큰 갱신 |
| `chatgpt.com`, `auth.openai.com` | Codex 사용량 조회(기본 5분마다), 토큰 갱신 |
| `api.github.com`, `github.com` | 하루 한 번(또는 **지금 확인**을 눌렀을 때) 새 버전을 확인합니다. 자동 업데이트를 꺼 두지 않았다면 exe를 그 자리에서 교체합니다. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 자동 실행을 켰을 때만 |
| `HKCU\Software\ClaudeCodexUsageWinTray` | 갱신 간격이나 업데이트 설정을 바꿨을 때만 |
| `claude auth login`, `codex login`, Claude·Codex 앱 | 메뉴에서 로그인 항목을 눌렀을 때만. CLI가 있으면 CLI로, 없으면 앱을 열고, 둘 다 없으면 내려받기 페이지를 엽니다. |

`CLAUDE_CONFIG_DIR`이나 `CODEX_HOME`을 설정해 두셨다면 CLI처럼 그 위치를 봅니다.

## 자주 묻는 질문

<details>
<summary><b><code>-</code>만 있는 칸 하나만 보여요</b></summary>
<br>
아직 아무것도 로그인되어 있지 않은 상태입니다. 우클릭해서 로그인 메뉴를 누르면 CLI 로그인 창이 열리고, CLI가 없으면 앱이 열려요.
</details>

<details>
<summary><b>숫자가 흐릿하게 보여요</b></summary>
<br>
마지막 조회가 실패해서(네트워크 문제나 요청이 너무 잦은 경우) 직전 값을 보여 주는 중입니다. 잠시 뒤 알아서 다시 확인해요. 클릭하면 이유가 나옵니다.
</details>

<details>
<summary><b>"Windows의 PC 보호" 경고가 떠요</b></summary>
<br>
아직 코드 서명이 없어서 처음 실행할 때 SmartScreen이 경고합니다. <b>추가 정보</b>를 누른 뒤 <b>실행</b>을 누르세요.
</details>

<details>
<summary><b>작업 표시줄에 아무것도 안 나와요</b></summary>
<br>
작업 표시줄을 세로로 둔 경우(Windows 10에서 왼쪽·오른쪽 배치)는 지원하지 않습니다. 그게 아니라면 작업 관리자에서 프로그램이 실행 중인지 확인해 주세요.
</details>

<details>
<summary><b>지우려면 어떻게 하나요?</b></summary>
<br>
우클릭해서 자동 실행을 끄고 종료한 다음 exe 파일을 지우면 끝입니다. 갱신 간격이나 업데이트 설정을 바꾼 적이 있다면 <code>HKCU\Software\ClaudeCodexUsageWinTray</code>도 지워 주세요.
</details>

## 라이선스

[MIT](LICENSE)
