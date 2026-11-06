# CI Compose smoke — sửa bootstrap schema và lỗi dây chuyền

2026-11-07. Phạm vi: CI workflow, CI-only Compose override và smoke script; không sửa API/nghiệp vụ/Payment/Bill/Mobile UI. Không chạy stack, SQL/migration, deploy, reset volume hoặc commit/push trong lượt này.

## Bằng chứng và nguyên nhân

Log người dùng: Identity/tenant/branch/menu checks PASS, lần đầu POST Orders trả 500. Sau đó Order ID rỗng, các check Payment/Kitchen/QR phụ thuộc thất bại, `grep -c $ORDER` thành `grep -c` và in usage lặp lại. Tổng 99 FAIL không phải bằng chứng 99 lỗi nghiệp vụ độc lập.

Đối chiếu source hiện tại:

- Workflow tạo stack trên GitHub-hosted runner mới và init env bằng secret tạm.
- PostgreSQL bootstrap chỉ tạo database/schema/roles, không tạo bảng nghiệp vụ Ordering/Kitchen.
- Compose gốc override `Database__MigrateOnStartup=false` riêng Order/Kitchen; workflow cũ không có bước migrate khác. Các service khác kế thừa config riêng hiện có.
- Vì vậy CI thiếu bước tạo bảng Order/Kitchen trước smoke. Đây là lỗi cấu hình xác minh được từ source, khớp lỗi 500/query ở log. **File được gửi không có exception SQL của service**, nên chưa tuyên bố đã tái hiện chính xác SQLSTATE/table name trên run GitHub đó.

## Thay đổi

| File | Nội dung |
| --- | --- |
| `docker-compose.ci.yml` | Chỉ Order/Kitchen bật startup migration từ biến CI bắt buộc `AICOS_CI_MIGRATE_ON_STARTUP`; thiếu biến thì Compose từ chối parse. Không có default true |
| `.github/workflows/docker-images.yml` | Compose-smoke job dùng `COMPOSE_FILE=base:ci override`, biến opt-in=true và project name theo run ID/attempt; startup/logs/cleanup dùng cùng merged config. Generate secret tạm → offline script tests → config quiet → build/start → smoke |
| Workflow path filters | Thay đổi CI override/smoke script/tests/workflow cũng trigger kiểm tra trên push main/PR, không bỏ sót script-only changes |
| `scripts/smoke-docker.sh` | Fail ngay check sai đầu tiên, không chạy tiếp 99 lỗi dây chuyền; kiểm tra GUID Order trước dùng; thêm status checks cho các create trước đây chưa kiểm tra; grep dynamic dùng fixed-string/quoted/-- |
| Smoke helpers | Status lấy từ dòng cuối để không in response nhiều dòng; progress login retry đi stderr, không trộn vào JSON/token capture; không in raw login body/password length. Helper tạo thêm Order kiểm tra create/cash và trả ID sạch; lỗi subshell truyền về parent |
| `scripts/tests/smoke-docker-test.sh` | 8 scenario hermetic, fake curl/sleep, private temp copy; không dùng Docker, network, database hoặc credentials thật |
| `README.md` | Thêm link runbook và sửa dòng cũ nói chưa có CI, phù hợp workflow hiện có |

Không sửa `docker-compose.yml`, appsettings hoặc các main-runtime overrides. Ordering/Kitchen ở config gốc vẫn migration=false. File `.ci.yml` không tự được Compose local nạp, không thêm biến opt-in vào `.env.example`. Đừng export COMPOSE_FILE/CI opt-in vào môi trường vận hành.

CI migration chỉ chạy **khi workflow mới được thực thi** trên stack/database/volume tạm của runner; áp dụng các EF migrations hiện có, không bật Cup Serving/recovery hoặc thay đổi financial rules. Việc sửa file workflow không có nghĩa đã chạy migration local hoặc trên database chính.

## Kiểm tra thực sự đã chạy

| Kiểm tra | Kết quả |
| --- | --- |
| Bash syntax smoke script và test harness | PASS bằng Git Bash, kiểm từng file |
| HTTP create trả 500 | PASS: dừng đúng check đầu, không gọi payment phụ thuộc, không grep usage |
| HTTP 500 body nhiều dòng | PASS: status vẫn là 500, không lộ body vào log |
| Create 201 thiếu ID | PASS: abort trước payment |
| Create 201 ID sai định dạng | PASS: abort trước payment |
| Create 201/ID hợp lệ | PASS: đi tới check phụ thuộc tiếp theo; test cố tình inject lỗi tiếp theo để chặn full flow. Không phải API tạo Order thật đã PASS |
| Login 429 rồi 200 | PASS: retry stderr không làm hỏng token/status capture, sau đó đến create-order check |
| Login 500 / 200 thiếu token | PASS: abort, không lộ dummy password/token/response body |
| Docker Compose merged config | PASS bằng env file giả tạm, không dump resolved secrets: base Order/Kitchen=false, CI=true |
| Override thiếu opt-in | PASS: Compose reject exit 1 theo kỳ vọng |
| Git diff whitespace | PASS |
| Full Docker build/start/E2E và GitHub Actions rerun | **NOT RUN** trong lượt này; không có image/container/service nào được khởi động hoặc migrate |
| .NET/Flutter suites | Không chạy lại: không sửa source ứng dụng. Các số PASS ở báo cáo Serving là lịch sử, không dùng làm bằng chứng CI stack xanh |

Tổng **8 script regression scenarios PASS**, đều dùng fake HTTP, tách biệt Compose config validation. Compose parser xác minh YAML Compose; workflow đã review cấu trúc, chưa chạy actionlint/GitHub execution. Module Node `yaml` không có ở máy hiện tại, không cài package hoặc thay dependency chỉ để lint file workflow.

Lần validation guard đầu, PowerShell ErrorActionPreference=Stop bắt native stderr của lỗi từ chối biến (đúng kỳ vọng) trước assertion; chạy lại riêng negative check với xử lý exit code đã PASS. Không đổi guard để né validation.

## Xác minh khi workflow được chạy lại

1. Commit/push do người dùng chủ động thực hiện, không tự làm ở lượt này. Run mới phải có bước offline regression và config quiet.
2. Build/start phải hoàn tất migration trong Ordering/Kitchen **ở CI**, trước health/smoke. Nếu lỗi migration hoặc schema, xem Logs on failure ngay; không khắc phục bằng bật migration trên DB vận hành.
3. Kiểm tra POST counter Order thực sự trả 201 và ID không rỗng, sau đó mới đánh giá Payment/Kitchen/Serving/QR. Script mới sẽ dừng đúng failure đầu; log thiếu bảng trước đó không bị spam thêm hàng chục request sai ID.
4. Nếu còn lỗi, lấy run SHA + failing step + log Order/Kitchen. Không suy toàn bộ smoke PASS từ config hoặc mock tests. Những lỗi nghiệp vụ/async khác phải đánh giá riêng từ run mới.

Rollback cấu hình: revert CI override/workflow/script changes có chủ đích nếu cần; không dùng reset DB/volume hoặc migration Down. Không có database rollback cần chạy vì lượt này không thay database. Không tự chạy `docker compose up`/`down -v` local để tái hiện CI trên stack đang có.
