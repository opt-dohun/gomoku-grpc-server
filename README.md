# gRPC 기반 실시간 오목 게임 (Gomoku)

gRPC와 Server-Sent Events (SSE) 기술을 결합하여 가볍고 빠른 실시간 멀티플레이어 환경을 구축한 오목 게임 프로젝트입니다.  
포트폴리오에 활용하기 적합하도록 비동기 통신 처리, 동시성 안전 설계, 그리고 Docker 형태의 배포 모델을 적용하였습니다.

## 시스템 아키텍처

본 프로젝트는 **gRPC 게임 백엔드 서버**와 **웹 클라이언트 서비스**가 네트워크를 통해 통신하는 구조입니다. 실시간 전송을 위해 **gRPC 서버 스트리밍**과 **SSE(Server-Sent Events)**를 사용하였습니다.

## 실시간 흐름 시퀀스

실시간 중계가 진행되는 시퀀스 흐름도입니다.

```
플레이어 1 ──→ gRPC Unary (PlaceStone) ──→ GameRoom (로직 처리)
                                                  │
                  ┌────────────────────────────────┘
                  ▼
         NotifyAllWatcherAsync()
                  │
          ┌───────┴───────┐
          ▼               ▼
      Stream 1        Stream 2
      (P1에게)        (P2에게)
   GameState 전송   GameState 전송
```

## 기술적 도전 및 최적화

### 1. 방과 SSE 구독자 컬렉션의 동시성 제어

- **문제**: ASP.NET Core는 여러 요청을 동시에 처리합니다. 방 생성·조회와 SSE 구독/해제가 겹칠 때 일반 컬렉션을 보호 없이 열거하거나 수정하면 경쟁 상태와 컬렉션 수정 예외가 발생할 수 있습니다. 특히 SSE 구독자는 연결이 끊긴 특정 항목을 찾아 제거해야 합니다.
- **설계**:
  - 방 ID에서 `GameRoom`을 찾는 맵은 `ConcurrentDictionary<string, GameRoom>`으로 관리합니다. 이 컬렉션은 방 맵의 동시 접근만 안전하게 하며, 안에 저장된 `GameRoom`의 보드·플레이어 상태까지 자동으로 보호하지는 않습니다.
  - 각 방의 SSE 구독자는 `List<Channel<GameState>>`로 보관합니다. 구독 등록, 해제, 브로드캐스트 중 목록 열거는 같은 `RoomLock`으로 보호합니다. 연결별 `Channel<GameState>`를 사용하므로 특정 구독을 찾아 제거할 수 있습니다.
  - 임계 구역에서는 구독자 목록을 순회해 `TryWrite`로 상태를 큐에 넣는 작업만 합니다. SSE 응답 본문을 쓰고 flush하는 네트워크 I/O는 락을 벗어난 구독자별 읽기 루프에서 수행합니다. 핵심은 락을 없애는 것이 아니라 **락 안에서 대기 가능한 네트워크 작업을 하지 않는 것**입니다.

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

- **동기화 경계**: `RoomLock`은 SSE 구독자 등록·해제와 브로드캐스트 시 목록 접근을 직렬화합니다. 방 맵의 동기화와 게임 보드·턴 상태의 동기화는 서로 다른 책임으로 분리했습니다. 같은 방의 동시 착수 순서까지 보장하려면 상태 변경 경계에도 방별 직렬화 정책을 적용해야 합니다.

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

- **운영 시 고려사항**: 구독자별 무제한 채널은 소비가 느린 연결에서 대기 데이터가 누적될 수 있습니다. 서비스 규모와 메시지 의미에 맞춰 bounded channel 용량과 초과 정책을 정하고, 중간 이벤트 유실이 허용되는 경우에만 최신 상태 병합(coalescing)을 적용합니다.

### 3. PAAR 구조의 한계 극복 및 통신 지연 개선 (실시간 상태 동기화)

- **문제 (PAAR 기술의 한계)**:
  기존 게임 화면은 1초 주기로 서버에 상태를 묻는 PAAR(Polling And Ajax Request) 기반 구조로 동작했습니다. 이로 인해 내가 착수한 돌은 즉시 반영(중앙값 32.5ms)되지만, 상대방 화면에는 다음 폴링 주기까지 대기해야 하므로 **최대 877ms (중앙값 588ms)**의 지연이 발생했습니다.
- **해결 과정 및 지연 수정**:
  이를 해결하기 위해 기존 1초 주기 폴링 코드를 완전히 제거하고, **gRPC 스트림과 SSE(Server-Sent Events) 구독**을 직접 화면에 연결했습니다. 서버에서 상태가 변경될 때마다 즉시 클라이언트로 상태를 밀어넣는(Push) 방식으로 변경하여, 상대방 화면 반영 지연을 획기적으로 개선하고 불필요한 HTTP GET 요청 부하를 없앴습니다.
- **지표 정보 및 시각 자료**:
  _(개선 전 폴링 지연 문제 재현 화면)_  
  ![두 플레이어 화면의 실제 착수 반영 차이](01-polling-delay.gif)

### 4. 수신 큐(Receive Queue)를 통한 Lock 최소화 설계

- **문제**: 실시간 통신 시 여러 사용자의 스트림에 동시에 메시지를 브로드캐스트하는 과정에서 컬렉션 전체에 강한 Lock을 걸게 되면 성능 병목이 발생합니다.
- **해결**: 이를 개선하기 위해 **수신 큐(Receive Queue)** 구조를 도입하여, 데이터를 전달하는 과정에서 발생하는 Lock 점유 시간을 최소한으로 줄였습니다. 비동기 환경에서도 동시성 예외 없이 안전하고 빠른 실시간 통신이 가능하도록 설계했습니다.

### 5. 반응형 플레이 화면 개선 (모바일 해상도)

- **문제**: 390px의 작은 모바일 뷰포트에서 고정된 600px 크기의 보드가 가로로 잘려 보이는(Overflow) 현상이 있었습니다.  
  ![작은 화면 보드 잘림 현상](02-mobile-overflow.gif)
- **해결**: 뷰포트 크기에 맞춰 보드 크기가 유동적으로 변하는 반응형 설계를 적용하고, 변경된 표시 크기 비율을 계산해 클릭 좌표(x, y)를 보드 내부 좌표로 정확히 변환하도록 CSS와 스크립트를 수정했습니다.

## 기술 스택

- **Language & Platform**: C# (.NET 9.0)
- **Protocol**: gRPC (Protobuf v3), HTTP/2 (Unencrypted h2c)
- **Network**: Server-Sent Events (SSE), gRPC Server Streaming
- **DevOps**: Docker, Docker Compose v2

## 빠른 시작

```bash
# 1. 저장소 클론
git clone https://github.com/your-repo/gomoku-grpc.git
cd gomoku-grpc

# 2. Docker Compose 빌드 및 실행
docker compose up --build -d
```

- **gRPC 게임 백엔드**: `http://localhost:5224`
- **오목 게임 웹 로비**: `http://localhost:5051`에 접속하여 즐길 수 있습니다.
