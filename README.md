# gRPC 기반 실시간 오목 게임 (Gomoku)

gRPC와 Server-Sent Events (SSE) 기술을 결합하여 가볍고 빠른 실시간 멀티플레이어 환경을 구축한 오목 게임 프로젝트입니다.  
포트폴리오에 활용하기 적합하도록 비동기 통신 처리, 동시성 안전 설계, 그리고 Docker 형태의 배포 모델을 적용하였습니다.


## 시스템 아키텍처

본 프로젝트는 **gRPC 게임 백엔드 서버**와 **웹 클라이언트 서비스**가 네트워크를 통해 통신하는 구조입니다. 실시간 전송을 위해 **gRPC 서버 스트리밍**과 **SSE(Server-Sent Events)**를 사용하였습니다.


##  실시간 흐름 시퀀스

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

### 1. 방 목록 및 참여자 리스트 관리 
- **문제**: 
  - 서버에서 gRPC 스트림 연결을 유지하며 클라이언트에 게임 상태를 실시간으로 전송하기 위해, 방별 커넥션 스트림을 저장하는 컬렉션(_watchers)에 여러 스레드(유저 행동, 관전자 입/퇴장)가 동시에 접근할 때 InvalidOperationException과 같은 컬렉션 수정 예외가 발생할 위험이 있었습니다.
- **접근 및 해결**:
    - 방별 감시대상을 저장하는 리스트 자료형으로 ConcurrentBag<T>을 검토했으나, 여러 스레드가 접근하는 환경에서는 안전하지만 요소의 순서와 상관없이 특정 요소만 Remove하는 연산을 지원하지 않았습니다. 중도 퇴장하거나 연결이 해제된 특정 스트림만 정확히 찾아 제거해야 하므로, 임의 삭제가 용이한 일반 List<T>를 사용하되 동시 접근을 방지하기 위해 lock을 도입하였습니다.
      ```csharp
      // 일반 List<T>를 사용하고 lock을 적용하여 특정 스트림을 중도 퇴장/해제 시 임의 삭제
      lock (watchers)
      {
          watchers.Remove(writer);
      }
      ```
    - 세마포어가 아닌 lock을 선택한 이유는, 임계 구역의 목적이 List에 대한 단순한 추가/삭제 접근을 관리하기 위한 것으로 간결한 연산만 진행하며, 내부에 여러 스레드가 진입하여 비동기 연산을 실행하는 구조가 아니기 때문입니다. 실제 비동기 전송은 락 외부에서 복사된 스냅샷 객체를 통해 진행되므로, 비동기 대기가 필요한 SemaphoreSlim 대신 가볍고 빠른 동기식 lock(Monitor)을 사용해 최적화하였습니다.
      ```csharp
      // 락 내부에서는 동기적으로 스냅샷(복사본)만 빠르게 생성
      List<IServerStreamWriter<GameState>> snapshot;
      lock (watchers)
      {
          snapshot = watchers.ToList();
      }
      // 실제 비동기 전송은 락 외부에서 복사된 객체를 통해 비동기로 진행 (WaitAsync 사용)
      await Task.WhenAll(snapshot.Select(w => w.WriteAsync(gameState).WaitAsync(cancellationToken)));
      ```
    - 여러 방이 실시간으로 생성되고 삭제되는 멀티스레드 환경에서 방 목록 자체를 스레드 안전하게 관리하기 위해 ConcurrentDictionary를 사용했습니다.
      ```csharp
      // 방 식별자를 Key로 하여 스트림 리스트를 스레드 안전하게 관리
      private static readonly ConcurrentDictionary<string, List<IServerStreamWriter<GameState>>> _watchers = new();
      ```
    - 방 목록에 대한 관리는 `ConcurrentDictionary`를 사용한 이유는 방 식별자를 Key로 하여 Value로 스트림 리스트를 관리하는 구조가 깨지지 않도록 하는 것을 목적으로 하였습니다. 또한 `ConcurrentBag<T>`과 같은 스레드 안전 컬렉션을 사용하지 않은 이유는 특정 요소를 제거하는 Remove 연산의 시간 복잡도가 O(n)으로, 감시자 수가 많아질수록 성능이 저하되기 때문에 O(1)로 접근 가능한 Dictionary를 선택했습니다.
      ```csharp
      // 방 ID(Key)를 기반으로 O(1) 시간 복잡도로 빠르게 감시자 목록 조회
      if (_watchers.TryGetValue(roomId, out var watchers))
      {
          // ...
      }
      ```


### 2. CancellationToken을 통한 리소스 누수 방지
- **문제**: 
  - 기존에는 연결된 감시자의 세션을 대기하기 위해 1초마다 주기적으로 확인하는 루프를 사용하여 타이머 스레드 및 CPU 자원을 지속적으로 낭비하고 있었습니다.
  - 비동기 데이터 쓰기 작업에서 단순 `WaitAsync` 대기를 적용하여, 타임아웃 시 대기하는 태스크는 끝났으나 실제 소켓 수준의 전송은 백그라운드에서 취소되지 않고 지속되는 문제가 있었습니다.
  - 브라우저와 웹 서버(SSE) 간의 연결이 끊어져도 웹 서버와 gRPC 서버 간의 스트림이 취소되지 않고 유지되어 서버 리소스가 낭비되었습니다.

- **접근 및 해결**:
  - 1초 주기 폴링 방식 대신 `CancellationToken`이 취소될 때까지 확인하기 위해 쓰레드를 할당받고 조회하는 과정없이 `await Task.Delay(Timeout.InfiniteTimeSpan, token)`를 사용하여 IOCP 큐 내부에서 비동기적으로 확인하도록 최적화하였습니다. 덕분에 연결 해제 즉시 정리할 수 있었습니다.
     ```csharp
     // GameService.cs - WatchGame
     try
     {
         // 1초 폴링 대신 취소 이벤트 발생 시까지 대기 (CPU 자원 소모 최소화)
         await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
     }
     catch (OperationCanceledException)
     {
         _logger.LogInformation("클라이언트 연결이 끊어졌습니다.");
     }
     finally
     {
         // 모든 상황에서 안전하게 제거되도록 보장
         RemoveWatcher(request.RoomId, responseStream);
     }
     ```

  - 기존 작성하였던 `WriteAsync().WaitAsync(token)`은 대기만 취소할 뿐 OS의 비동기 I/O(IOCP)를 중단하지 못하는 문제를 해결하기 위해, `WriteAsync` 내부에 직접 `cancellationToken`을 인자로 전달하여 소켓 커널 버퍼 수준의 쓰기 작업을 중단하고 모든 리소스를 반환하도록 수정했습니다.
     ```csharp
     // GameService.cs - TryNotifyWatcherAsync
     try
     {
         // WriteAsync 메서드 매개변수로 직접 CancellationToken을 넘겨 소켓 및 커널/IOCP 수준의 리소스 즉시 해제
         await writer.WriteAsync(gameState, cancellationToken);
         return true;
     }
     catch (OperationCanceledException)
     {
         _logger.LogWarning("방 {RoomId} 감시자 알림 전송 타임아웃 (5초 초과)", roomId);
         return false;
     }
     ```

  - 사용자가 웹 브라우저를 닫아 발생하는 HTTP 연결 중단 신호(`context.RequestAborted`)를 gRPC 클라이언트 채널을 거쳐 gRPC 서버까지 직접 연결(Chaining)하여, 전체 통신 파이프라인에서 끊김 없는 동시 해제가 이뤄지도록 구성하였습니다.
     ```csharp
     // 1) GomokuClient.Web/Program.cs - SSE 엔드포인트
     var ct = context.RequestAborted;
     using var stream = gameClient.WatchGameStream(roomId, ct); // HTTP 취소 토큰(ct) 전달
     
     // 2) GomokuClient.Web/Services/GrpcGameClient.cs
     public Grpc.Core.AsyncServerStreamingCall<GameState> WatchGameStream(string roomId, CancellationToken cancellationToken = default)
     {
         // gRPC 클라이언트 호출 시 취소 토큰 체이닝
         return _client.WatchGame(new RoomInfo { RoomId = roomId }, cancellationToken: cancellationToken);
     }
     ```
  

### 3. PAAR 구조의 한계 극복 및 통신 지연 개선 (실시간 상태 동기화)
- **문제 (PAAR 기술의 한계)**: 
  기존 게임 화면은 1초 주기로 서버에 상태를 묻는 PAAR(Polling And Ajax Request) 기반 구조로 동작했습니다. 이로 인해 내가 착수한 돌은 즉시 반영(중앙값 32.5ms)되지만, 상대방 화면에는 다음 폴링 주기까지 대기해야 하므로 **최대 877ms (중앙값 588ms)**의 지연이 발생했습니다.
- **해결 과정 및 지연 수정**: 
  이를 해결하기 위해 기존 1초 주기 폴링 코드를 완전히 제거하고, **gRPC 스트림과 SSE(Server-Sent Events) 구독**을 직접 화면에 연결했습니다. 서버에서 상태가 변경될 때마다 즉시 클라이언트로 상태를 밀어넣는(Push) 방식으로 변경하여, 상대방 화면 반영 지연을 획기적으로 개선하고 불필요한 HTTP GET 요청 부하를 없앴습니다.
- **지표 정보 및 시각 자료**:
  *(개선 전 폴링 지연 문제 재현 화면)*  
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
