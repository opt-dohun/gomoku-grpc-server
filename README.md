# gRPC 기반 실시간 오목 게임 (Gomoku)

gRPC와 Server-Sent Events (SSE) 기술을 결합하여 가볍고 빠른 실시간 멀티플레이어 환경을 구축한 오목 게임 프로젝트입니다.  
포트폴리오에 활용하기 적합하도록 비동기 통신 처리, 동시성 안전 설계, 그리고 Docker Compose 기반 배포 모델을 적용하였습니다.

## 통신 시퀀스 흐름

통신 시퀀스 흐름도입니다.

```
플레이어 1 ──→ gRPC Unary (PlaceStone) ──→ GameRoom (로직 처리)
                                                  │
                  ┌────────────────────────────────┘
                  ▼
         NotifyAllWatcherAsync()
                  │
          ┌───────┴───────┐
          ▼               ▼
       SSE 연결 1      SSE 연결 2
       (P1에게)         (P2에게)
   상태 이벤트 전송   상태 이벤트 전송
```

## 기술적 도전 및 최적화

### 1. 방과 SSE 구독자 컬렉션의 동시성 제어

- **문제**: ASP.NET Core는 여러 요청을 동시에 처리합니다. 방 생성·조회와 SSE 구독/해제가 겹칠 때 일반 컬렉션을 보호 없이 열거하거나 수정하면 경쟁 상태와 컬렉션 수정 예외가 발생할 수 있습니다. 특히 SSE 구독자는 연결이 끊긴 특정 항목을 찾아 제거해야 합니다.
- **해결 및 구현**:
  - 방 ID에서 `GameRoom`을 찾는 맵은 `ConcurrentDictionary<string, GameRoom>`으로 관리합니다. 이 컬렉션은 방 맵의 동시 접근만 안전하게 하며, 안에 저장된 `GameRoom`의 보드·플레이어 상태까지 자동으로 보호하지는 않습니다.
  - 각 방의 SSE 구독자는 `List<Channel<GameState>>`로 보관합니다. 구독 등록, 해제, 브로드캐스트 중 목록 열거는 같은 `RoomLock`으로 보호합니다. 연결별 `Channel<GameState>`를 사용하므로 특정 구독을 찾아 제거할 수 있습니다.
  - 임계 구역에서는 구독자 목록을 순회해 `TryWrite`로 상태를 큐에 넣는 작업만 합니다. SSE 응답 본문을 쓰고 flush하는 네트워크 I/O는 락을 벗어난 구독자별 읽기 루프에서 수행합니다. 결과적으로 **락 안에서 대기 가능한 네트워크 작업을 배재**하였습니다.

  ```csharp
  // GameRoom.cs — 방별 구독자 목록
  public readonly object RoomLock = new();
  public readonly List<Channel<GameState>> Watchers = new();

  // BroadcastGameState — 락 안에서는 각 구독자 큐에 enqueue만 수행
  lock (RoomLock)
  {
      foreach (var watcher in Watchers)
          watcher.Writer.TryWrite(state);
  }
  ```

  - `RoomLock`은 상호 배제로 구독자 목록의 동시 접근을 막으며, 요청 도착 순서를 보장하지는 않습니다.

핵심은 **수신 큐(Channel) + 짧은 임계 구역**으로 구독자 목록을 보호하고, 네트워크 I/O를 락 밖에서 수행해 락 점유 시간을 줄이는 것입니다.

### 2. SSE 연결 해제 시 CancellationToken으로 구독 정리

- **문제**: SSE는 연결이 열린 동안 서버가 구독자별 채널을 유지합니다. 브라우저가 페이지를 닫거나 네트워크가 끊겼을 때 구독자를 정리하지 않으면 방의 watcher 목록에 오래된 구독이 남고, 이후 브로드캐스트 때마다 불필요한 큐에도 상태를 쓰게 됩니다.
- **접근 및 해결**: SSE 요청의 `HttpContext.RequestAborted`를 `Channel.Reader.ReadAllAsync`에 전달합니다. 요청이 취소되면 읽기 루프가 종료되고, `finally`에서 해당 채널을 같은 `RoomLock` 안에서 watcher 목록에서 제거합니다. 브라우저 쪽에서도 페이지 이탈·게임 종료 시 `EventSource.close()`를 호출합니다.

  ```csharp
  // Program.cs — SSE 구독 채널 생성 및 초기 상태 등록
  var channel = System.Threading.Channels.Channel.CreateUnbounded<GomokuGame.Proto.GameState>();
  lock (room.RoomLock)
  {
      room.Watchers.Add(channel);
      channel.Writer.TryWrite(gameLogic.GetGameState(room));
  }

  try
  {
      await foreach (var state in channel.Reader.ReadAllAsync(context.RequestAborted))
      {
          var json = Google.Protobuf.JsonFormatter.Default.Format(state);
          await context.Response.WriteAsync($"data: {json}\n\n");
          await context.Response.Body.FlushAsync();
      }
  }
  catch (OperationCanceledException)
  {
      // 브라우저 연결 해제
  }
  finally
  {
      lock (room.RoomLock)
      {
          room.Watchers.Remove(channel);
      }
  }
  ```

- **취소 전파 범위**: 웹 브라우저는 상태를 SSE로 구독하고, 착수 명령은 웹 서버에서 gRPC unary 호출로 전달합니다. `RequestAborted`는 SSE 채널 읽기 루프에 연결되어 구독 종료와 정리를 수행합니다. SSE와 gRPC unary는 별도의 요청 경로이므로, HTTP 연결 취소가 gRPC 스트리밍 호출까지 전파된다고 표현하지 않습니다. CancellationToken은 협력적 취소 신호로 사용합니다.

- **운영 시 고려사항**: 구독자별 무제한 채널은 소비가 느린 연결에서 대기 데이터가 누적될 수 있습니다. 서비스 규모와 메시지 의미에 맞춰 bounded channel 용량과 초과 정책을 수립하는 것이 중요합니다.

### 3. 동시성 안전 설계의 범위

현재 동시성 보호는 **방 맵과 SSE 구독자 목록**에 적용되어 있으며, 게임 규칙 검증과 보드·턴 상태 변경은 별도의 책임으로 구분합니다.

- **게임 로직의 책임**: 도메인 계층의 `GameLogicService`가 게임 진행 여부·플레이어의 턴·착수 위치를 검사하고 보드·턴 상태와 승패를 갱신합니다.
- **현재 한계**: 검사와 상태 변경이 하나의 임계 구역으로 묶여 있지 않아, 동시 착수 요청이 모두 사전 검사를 통과할 수 있습니다. 턴 검증만으로 동시성 안전성이 보장되지는 않습니다.
- **보완 방향**: 방별 잠금이나 처리 큐를 도입해 검사부터 상태 변경·결과 스냅샷 생성까지 하나의 처리 단위로 보호해야 합니다.

#### gRPC 읽기 RPC 측정

`GetRoomList`와 `GetGameState` gRPC Unary 읽기 RPC를 60초 동안 측정한 로컬 기준 결과입니다. 그래프는 원시 k6 결과에서 측정 구간의 초기화 호출을 제외하고, 10초 구간별 p95 지연을 계산해 표시합니다.

![k6 gRPC 읽기 RPC의 10초 구간별 p95 지연](loadtest/artifacts/k6-grpc-read-2026-10-06-time-series.svg)

| RPC            | 표본 수 | 성공률 | p95 지연 | p99 지연 |
| -------------- | ------: | -----: | -------: | -------: |
| `GetRoomList`  |     301 |   100% | 0.549 ms | 0.904 ms |
| `GetGameState` |     301 |   100% | 0.741 ms | 0.881 ms |

- **실행 시나리오**: k6 0.54.0, `RATE=5` 반복/초(반복당 RPC 2회), 60초, 초기 VU 5개 최대 10까지 증가
- 이 시나리오는 gRPC 읽기 RPC만 측정합니다. `PlaceStone`부터 SSE 전달까지의 종단 간 측정은 현재 Kestrel의 HTTP/2 전용 listener와 xk6-sse 간 TLS ALPN 협상 문제로 완료되지 않았습니다. 자세한 조건과 재현 방법은 [부하 테스트 안내](loadtest/README.md)를 참고하세요.

### 4. 계정 인증과 게임 접근 권한 분리

- **문제**:
  – 초기 API 설계 시 방 ID와 참가자 ID·닉네임을 URL이나 요청 본문으로 전달했습니다. 그러나 클라이언트가 전달한 값은 변경할 수 있으므로, 요청자가 해당 방의 참가자인지, 또 어느 참가자의 권한으로 행동하는지 판단할 경우, 특히 참가자 ID를 그대로 게임 서버 요청에 사용하면 계정 인증과 게임 내 권한 검증의 신뢰성을 보장하기 어려울 것이라고 보았습니다.
- **접근 및 해결**:
  – ASP.NET Core Identity와 TOTP 2차 인증으로 로그인 계정을 확인하고, GameMembership에 방 생성·입장 시 게임 서버가 발급한 참가자 ID를 로그인 계정 ID·방 ID와 연결해 저장합니다. 참가자 ID는 브라우저에도 전달되지만, 웹 서버는 클라이언트가 보낸 값을 권한 증명으로 신뢰하지 않습니다. 착수 시 로그인 쿠키의 계정 ID와 요청의 방 ID로 참가 기록을 조회해, 저장된 참가자 ID를 게임 서버로 전달합니다. 게임 서버는 해당 ID가 현재 턴의 참가자인지 검사하며, 로그인 계정과 참가자 간 관계는 웹 서버에서 검사하도록 수정하였습니다.
  – 웹→게임 서버 gRPC 요청과 SSE 스트림 간에는 서비스 토큰을 적용하여, 게임 서버는 미들웨어에서 토큰을 검증하고, 토큰 파일이 없거나 형식이 잘못되면 거부하도록 구축하였습니다.
  – 이를 통해 웹을 거치는 요청은 클라이언트가 전달해주는 참가자 정보가 아니라 서버에 저장된 계정–방 참가 관계를 기준으로 처리하도록 개선했습니다

## 기술 스택

- **언어·프레임워크**: C# (.NET 9.0), ASP.NET Core (gRPC 서버 + MVC 웹 클라이언트)
- **프로토콜**: gRPC (Protobuf v3), HTTP/2 (h2c 평문 통신)
- **실시간 상태 전달**: Server-Sent Events (SSE) + `System.Threading.Channels.Channel<GameState>` 구독 큐
- **동시성 제어**: 방별 `object`(`RoomLock`) + `ConcurrentDictionary<string, GameRoom>` 방 맵
- **데이터 저장소**: in Memoery
- **배포·운영**: Docker, Docker Compose v2
- **부하 테스트**: k6 (gRPC 단항 읽기 RPC)

## 빠른 시작

```bash
git clone https://github.com/opt-dohun/gomoku-grpc-server.git
cd gomoku-grpc-server/gomoku-grpc
docker compose up --build -d
```

- gRPC 게임 백엔드: Compose 네트워크 내부의 `gomoku-server:5224`(호스트에 공개하지 않음)
- 오목 게임 웹 로비: `http://localhost:5051` (로컬 바인딩)

### Azure 배포 준비

Azure 단일 Ubuntu VM 인프라 Terraform 및 공개용 HTTPS Compose 초안은 [Azure 배포 가이드](gomoku-grpc/infra/azure/README.md)에 있습니다. [WSL Docker 로컬 HTTPS 검증](gomoku-grpc/deploy/README.md)은 완료했지만 **Azure 리소스 생성·공인 인증서 발급·외부 접속은 아직 검증하지 않았습니다.**

## 추후 후속 계획

게임 서버 gRPC는 웹 백엔드의 서비스 토큰을 검증하지만 사용자 계정·방 참가 권한을 서버에서 직접 확인하지는 않습니다. Compose 외부 노출은 제한했지만 내부 네트워크의 다른 프로세스가 토큰에 접근하지 못하도록 격리하고, 다중 호스트 운영 시 mTLS를 도입해야 합니다. 복구 코드, 이메일 확인, 2차 인증 재설정, OTP 재사용 방지, 계정/IP 속도 제한, 감사 로그도 아직 구현되지 않았습니다. DB 초기화는 `EnsureCreated` 방식이며 운영 배포 전에 migration, HTTPS, Data Protection 키 백업·회전 및 운영 구성을 검토해야 합니다. 설계와 보완 항목은 [TOTP 2차 인증 설계 문서](docs/security/totp-qr-mfa-design.md)를 참고하세요.
