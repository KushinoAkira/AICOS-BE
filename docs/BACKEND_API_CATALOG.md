# Danh mục API Backend main — 2026-11-03

Source: main d7ab6b7cb25634a2fb4635c10ebeb9232db27534. Kiểm kê bằng GET OpenAPI thực tế ở các port 5201–5211, đối chiếu route/group authorization trong source. Không gọi API nghiệp vụ có ghi, không tạo JWT/tài khoản.

**268 HTTP operations**: 263 của .NET + 5 của AI; gồm 10 metadata `_info`, nên còn 258 operation khác metadata, trong đó 4 AI placeholder. 127 GET và 141 non-GET. Không tính health/docs, 3 endpoint hạ tầng Gateway, SignalR transport hoặc ai-models standalone vào số 268.

Số operation chỉ là số cặp method/path đăng ký, không phải số test PASS hay mức độ hoàn thiện. Có route và có implementation chưa đồng nghĩa đã được kiểm thử E2E.

## Cách đọc quyền và contract

- Anonymous: không RequireAuthorization ở route/group; vẫn có thể kiểm tra OTP, QR capability hoặc HMAC trong handler. Nếu gửi JWT sai, Gateway vẫn trả 401.
- JWT: cần token hợp lệ; handler còn kiểm tra tenant, branch, ownership, trạng thái và quyền theo hành động. SuperAdmin không tự có quyền đọc dữ liệu tenant.
- Policy: PlatformAdmin=SuperAdmin; AdminOnly=BrandAdmin; Management=BrandAdmin hoặc BranchManager; Staff=BrandAdmin/BranchManager/Barista/Cashier; AvailabilityEditors=BrandAdmin/BranchManager/Barista; AuditViewer=SuperAdmin/BrandAdmin. AND nghĩa phải thỏa cả hai policy kế thừa.
- QR guest cần header X-Table-Session lấy từ POST /orders/qr/scan. Đây là token HMAC opaque, không dùng thay access JWT của nhân viên. QR payment-access cấp JWT Guest giới hạn session; không phải login staff.
- Cột Input liệt kê parameter OpenAPI và tên schema body; dấu * là required trong OpenAPI. Quy tắc nghiệp vụ/validator có thể yêu cầu thêm trường dù schema nullable.
- Cột Output ghi đúng OpenAPI đã khai báo. 62 operation không có typed response schema; các handler IResult có thể trả 201/204 dù OpenAPI chỉ ghi 200. Phải đọc handler trước khi viết adapter; ví dụ CreateOrder và CashPayment trả 201. Không coi response trống schema là body thực tế rỗng.
- GET không bảo đảm chỉ đọc. Receipt, cup stickers và payment detail có side effect ghi; không dùng làm smoke read-only hoặc tự retry mù quáng. Non-GET được đánh dấu có khả năng ghi, nhưng một số chỉ validate/tính toán.
- Runtime đã smoke health/metadata/auth âm ở bước trước. Việc audit này chỉ GET tài liệu OpenAPI; không thực thi 268 nghiệp vụ.

## Tổng hợp

| Service | Operations | GET | Non-GET | OpenAPI schemas |
| --- | ---: | ---: | ---: | ---: |
| ai | 5 | 1 | 4 | 1 |
| customer | 29 | 15 | 14 | 32 |
| identity | 65 | 27 | 38 | 75 |
| kitchen | 7 | 5 | 2 | 9 |
| menu | 18 | 8 | 10 | 23 |
| order | 40 | 20 | 20 | 42 |
| payment | 29 | 13 | 16 | 31 |
| reporting | 7 | 7 | 0 | 9 |
| table | 19 | 8 | 11 | 17 |
| workforce | 49 | 23 | 26 | 51 |

## ai

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/ai/_info` | Anonymous | - | 200: object/unspecified | [MapAicosApiGroup / info](../src/Services/Ai/app/main.py) |
| POST (placeholder 501) | `/api/v1/ai/attendance/verify` | JWT: SystemAdmin/Manager/Cashier/WarehouseStaff (role cũ) | - | 200: object/unspecified | [FastAPI router](../src/Services/Ai/app/routers/attendance.py) |
| POST (placeholder 501) | `/api/v1/ai/chat` | Guest/JWT optional (placeholder) | - | 200: object/unspecified | [FastAPI router](../src/Services/Ai/app/routers/chat.py) |
| POST (placeholder 501) | `/api/v1/ai/forecast/demand` | JWT: SystemAdmin/Manager/WarehouseStaff (role cũ) | - | 200: object/unspecified | [FastAPI router](../src/Services/Ai/app/routers/forecast.py) |
| POST (placeholder 501) | `/api/v1/ai/summary` | JWT: SystemAdmin/Manager/AiBusinessAssistant (role cũ) | - | 200: object/unspecified | [FastAPI router](../src/Services/Ai/app/routers/summary.py) |

## customer

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/customers` | Policy: Staff | query: search string; query: tier LoyaltyTier; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L53) |
| GET | `/api/v1/customers/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/customers/{id}` | Policy: Staff | path: id* string (uuid) | 200: CustomerResponse | [GetAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L54) |
| GET | `/api/v1/customers/{id}/loyalty` | Policy: Staff | path: id* string (uuid); query: page integer (int32); query: pageSize integer (int32) | 200: LoyaltySummaryResponse | [LoyaltyAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L55) |
| POST (có thể ghi) | `/api/v1/customers/{id}/points-adjustments` | Policy: Management | path: id* string (uuid); body: PointsAdjustmentRequest | 200: CustomerResponse | [AdjustPointsAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L56) |
| POST (có thể ghi) | `/api/v1/customers/{id}/redemptions` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid); body: RedeemPointsRequest | 200: không khai báo schema | [RedeemForAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Loyalty/RedemptionEndpoints.cs#L38) |
| GET | `/api/v1/customers/loyalty/rules` | Policy: Management | - | 200: LoyaltyRuleResponse | [GetAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Loyalty/LoyaltyRuleEndpoints.cs#L49) |
| PUT (có thể ghi) | `/api/v1/customers/loyalty/rules` | Policy: AdminOnly | body: LoyaltyRuleRequest | 200: LoyaltyRuleResponse | [UpdateAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Loyalty/LoyaltyRuleEndpoints.cs#L50) |
| GET | `/api/v1/customers/me` | JWT | - | 200: CustomerResponse | [MeAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L47) |
| PUT (có thể ghi) | `/api/v1/customers/me` | JWT | body: UpdateProfileRequest | 200: CustomerResponse | [UpdateMeAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L48) |
| GET | `/api/v1/customers/me/loyalty` | JWT | query: page integer (int32); query: pageSize integer (int32) | 200: LoyaltySummaryResponse | [MyLoyaltyAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L49) |
| GET | `/api/v1/customers/me/notifications` | JWT: Customer | query: unreadOnly boolean; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/NotificationEndpoints.cs#L31) |
| POST (có thể ghi) | `/api/v1/customers/me/notifications/{id}/read` | JWT: Customer | path: id* string (uuid) | 200: NotificationResponse | [ReadAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/NotificationEndpoints.cs#L34) |
| POST (có thể ghi) | `/api/v1/customers/me/notifications/read-all` | JWT: Customer | - | 200: UnreadCountResponse | [ReadAllAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/NotificationEndpoints.cs#L33) |
| GET | `/api/v1/customers/me/notifications/unread-count` | JWT: Customer | - | 200: UnreadCountResponse | [UnreadCountAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/NotificationEndpoints.cs#L32) |
| GET | `/api/v1/customers/me/preferences` | JWT | - | 200: PreferencesResponse | [MyPreferencesAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L50) |
| PUT (có thể ghi) | `/api/v1/customers/me/preferences` | JWT | body: PreferencesRequest | 200: PreferencesResponse | [UpdateMyPreferencesAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Customers/CustomerEndpoints.cs#L51) |
| POST (có thể ghi) | `/api/v1/customers/me/redemptions` | JWT: Customer | body: RedeemPointsRequest | 200: không khai báo schema | [RedeemMineAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Loyalty/RedemptionEndpoints.cs#L36) |
| GET | `/api/v1/customers/me/vouchers` | JWT: Customer | query: status VoucherStatus; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [MyVouchersAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Loyalty/RedemptionEndpoints.cs#L37) |
| GET | `/api/v1/customers/staff/notifications` | JWT: SuperAdmin OR BrandAdmin OR BranchManager OR Cashier OR Barista | query: unreadOnly boolean; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/StaffNotificationEndpoints.cs#L26) |
| POST (có thể ghi) | `/api/v1/customers/staff/notifications/{id}/read` | JWT: SuperAdmin OR BrandAdmin OR BranchManager OR Cashier OR Barista | path: id* string (uuid) | 200: không khai báo schema | [ReadAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/StaffNotificationEndpoints.cs#L29) |
| POST (có thể ghi) | `/api/v1/customers/staff/notifications/read-all` | JWT: SuperAdmin OR BrandAdmin OR BranchManager OR Cashier OR Barista | - | 200: UnreadCountResponse | [ReadAllAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/StaffNotificationEndpoints.cs#L28) |
| GET | `/api/v1/customers/staff/notifications/unread-count` | JWT: SuperAdmin OR BrandAdmin OR BranchManager OR Cashier OR Barista | - | 200: UnreadCountResponse | [UnreadCountAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Notifications/StaffNotificationEndpoints.cs#L27) |
| GET | `/api/v1/customers/vouchers` | Policy: Management | query: branchId string (uuid); query: status VoucherStatus; query: type VoucherType; query: search string; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L52) |
| POST (có thể ghi) | `/api/v1/customers/vouchers` | Policy: AdminOnly | body: CreateVoucherRequest | 200: không khai báo schema | [CreateAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L54) |
| GET | `/api/v1/customers/vouchers/{id}` | Policy: Management | path: id* string (uuid) | 200: VoucherResponse | [GetAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L53) |
| POST (có thể ghi) | `/api/v1/customers/vouchers/{id}/revoke` | Policy: AdminOnly | path: id* string (uuid); body: RevokeVoucherRequest | 200: VoucherResponse | [RevokeAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L55) |
| POST (có thể ghi) | `/api/v1/customers/vouchers/redeem` | Policy: Staff | body: RedeemVoucherRequest | 200: VoucherQuoteResponse | [RedeemAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L57) |
| POST (có thể ghi) | `/api/v1/customers/vouchers/validate` | Policy: Staff | body: ValidateVoucherRequest | 200: VoucherQuoteResponse | [ValidateAsync](../src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/Features/Vouchers/VoucherEndpoints.cs#L56) |

## identity

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/identity/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/identity/audit-logs` | Policy: AuditViewer | query: from string (date-time); query: to string (date-time); query: action string; query: entityType string; query: entityId string; query: actorUserId string (uuid); query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Audit/AuditEndpoints.cs#L48) |
| GET | `/api/v1/identity/audit-logs/{id}` | Policy: AuditViewer | path: id* string (uuid) | 200: AuditLogResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Audit/AuditEndpoints.cs#L49) |
| POST (có thể ghi) | `/api/v1/identity/auth/change-password` | JWT | body: ChangePasswordRequest | 200: không khai báo schema | [ChangePasswordAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L32) |
| POST (có thể ghi) | `/api/v1/identity/auth/forgot-password` | Anonymous | body: ForgotPasswordRequest | 200: không khai báo schema | [ForgotPasswordAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L33) |
| POST (có thể ghi) | `/api/v1/identity/auth/google` | Anonymous | body: GoogleLoginRequest | 200: AuthResponse | [GoogleAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L30) |
| POST (có thể ghi) | `/api/v1/identity/auth/login` | Anonymous | body: LoginRequest | 200: AuthResponse | [LoginAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L27) |
| POST (có thể ghi) | `/api/v1/identity/auth/logout` | Anonymous | body: RefreshTokenRequest | 200: không khai báo schema | [LogoutAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L29) |
| GET | `/api/v1/identity/auth/me` | JWT | - | 200: UserResponse | [MeAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L30) |
| PUT (có thể ghi) | `/api/v1/identity/auth/me` | JWT | body: UpdateProfileRequest | 200: UserResponse | [UpdateProfileAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L31) |
| POST (có thể ghi) | `/api/v1/identity/auth/me/delete` | JWT | body: DeleteMyDataRequest | 200: không khai báo schema | [DeleteMyDataAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L32) |
| POST (có thể ghi) | `/api/v1/identity/auth/refresh` | Anonymous | body: RefreshTokenRequest | 200: AuthResponse | [RefreshAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L28) |
| POST (có thể ghi) | `/api/v1/identity/auth/register` | Anonymous | body: RegisterRequest | 200: không khai báo schema | [RegisterAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L27) |
| POST (có thể ghi) | `/api/v1/identity/auth/register/resend` | Anonymous | body: ResendRegistrationRequest | 200: không khai báo schema | [ResendAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L29) |
| POST (có thể ghi) | `/api/v1/identity/auth/register/verify` | Anonymous | body: VerifyRegistrationRequest | 200: AuthResponse | [VerifyAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L28) |
| POST (có thể ghi) | `/api/v1/identity/auth/register-brand` | Anonymous | body: RegisterBrandRequest | 200: không khai báo schema | [RegisterBrandAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L59) |
| POST (có thể ghi) | `/api/v1/identity/auth/reset-password` | Anonymous | body: ResetPasswordRequest | 200: không khai báo schema | [ResetPasswordAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/AuthEndpoints.cs#L34) |
| POST (có thể ghi) | `/api/v1/identity/auth/select-brand` | JWT | body: SelectBrandRequest | 200: AuthResponse | [SelectBrandAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SignUpEndpoints.cs#L31) |
| GET | `/api/v1/identity/auth/sessions` | JWT | - | 200: array<SessionResponse> | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SessionEndpoints.cs#L23) |
| DELETE (có thể ghi) | `/api/v1/identity/auth/sessions/{id}` | JWT | path: id* string (uuid) | 200: không khai báo schema | [RevokeAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SessionEndpoints.cs#L24) |
| POST (có thể ghi) | `/api/v1/identity/auth/sessions/revoke-others` | JWT | - | 200: RevokedSessionsResponse | [RevokeOthersAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Auth/SessionEndpoints.cs#L25) |
| GET | `/api/v1/identity/branches` | Policy: AdminOnly | query: search string; query: status BranchStatus; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L26) |
| POST (có thể ghi) | `/api/v1/identity/branches` | Policy: AdminOnly | body: BranchRequest | 200: không khai báo schema | [CreateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L30) |
| GET | `/api/v1/identity/branches/{id}` | JWT | path: id* string (uuid) | 200: BranchResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L29) |
| PUT (có thể ghi) | `/api/v1/identity/branches/{id}` | Policy: AdminOnly | path: id* string (uuid); body: BranchRequest | 200: BranchResponse | [UpdateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L31) |
| PATCH (có thể ghi) | `/api/v1/identity/branches/{id}/status` | Policy: AdminOnly | path: id* string (uuid); body: ChangeBranchStatusRequest | 200: BranchResponse | [ChangeStatusAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L32) |
| GET | `/api/v1/identity/branches/active` | Anonymous | query: tenantCode string | 200: array<BranchResponse> | [ListActiveAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Branches/BranchEndpoints.cs#L27) |
| GET | `/api/v1/identity/brand/profile` | Policy: AdminOnly | - | 200: BrandProfileResponse | [GetProfileAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L65) |
| PUT (có thể ghi) | `/api/v1/identity/brand/profile` | Policy: AdminOnly | body: BrandProfileRequest | 200: BrandProfileResponse | [UpdateProfileAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L66) |
| GET | `/api/v1/identity/contact-messages` | Policy: PlatformAdmin | query: status ContactMessageStatus; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListMessagesAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L73) |
| POST (có thể ghi) | `/api/v1/identity/contact-messages/{id}/handled` | Policy: PlatformAdmin | path: id* string (uuid) | 200: ContactMessageResponse | [HandledAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L74) |
| GET | `/api/v1/identity/platform/overview` | Policy: PlatformAdmin | - | 200: PlatformOverviewResponse | [OverviewAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Platform/PlatformEndpoints.cs#L46) |
| GET | `/api/v1/identity/platform/revenue` | Policy: PlatformAdmin | query: from string (date-time); query: to string (date-time) | 200: RevenueReportResponse | [RevenueAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Platform/PlatformEndpoints.cs#L47) |
| GET | `/api/v1/identity/public/brands` | Anonymous | - | 200: array<PublicBrandCardResponse> | [PublicBrandsAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L69) |
| GET | `/api/v1/identity/public/brands/{code}` | Anonymous | path: code* string | 200: PublicBrandResponse | [PublicBrandAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L68) |
| POST (có thể ghi) | `/api/v1/identity/public/contact` | Anonymous | body: ContactRequest | 200: không khai báo schema | [ContactAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L70) |
| GET | `/api/v1/identity/public/subscription-plans` | Anonymous | - | 200: array<PublicPlanResponse> | [PublicListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L24) |
| GET | `/api/v1/identity/subscription` | Policy: AdminOnly | - | 200: MySubscriptionResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/MySubscriptionEndpoints.cs#L34) |
| GET | `/api/v1/identity/subscription/limits` | Policy: Management | - | 200: PlanLimitsResponse | [LimitsAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/MySubscriptionEndpoints.cs#L39) |
| GET | `/api/v1/identity/subscription/payments` | Policy: AdminOnly | query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [PaymentsAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/MySubscriptionEndpoints.cs#L35) |
| POST (có thể ghi) | `/api/v1/identity/subscription/renew` | Policy: AdminOnly | - | 200: RenewalResult | [RenewAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/MySubscriptionEndpoints.cs#L36) |
| GET | `/api/v1/identity/subscription-plans` | Anonymous | query: search string; query: isActive boolean; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L27) |
| POST (có thể ghi) | `/api/v1/identity/subscription-plans` | Policy: PlatformAdmin | body: PlanRequest | 200: không khai báo schema | [CreateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L29) |
| GET | `/api/v1/identity/subscription-plans/{id}` | Policy: PlatformAdmin | path: id* string (uuid) | 200: PlanResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L28) |
| PUT (có thể ghi) | `/api/v1/identity/subscription-plans/{id}` | Policy: PlatformAdmin | path: id* string (uuid); body: PlanRequest | 200: PlanResponse | [UpdateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L30) |
| PATCH (có thể ghi) | `/api/v1/identity/subscription-plans/{id}/status` | Policy: PlatformAdmin | path: id* string (uuid); body: PlanStatusRequest | 200: PlanResponse | [ChangeStatusAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/PlanEndpoints.cs#L31) |
| GET | `/api/v1/identity/subscriptions` | Policy: PlatformAdmin | query: health SubscriptionHealth; query: paymentStatus SubscriptionPaymentStatus; query: search string; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L33) |
| GET | `/api/v1/identity/tenants` | Policy: PlatformAdmin | query: search string; query: status TenantStatus; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/TenantEndpoints.cs#L25) |
| POST (có thể ghi) | `/api/v1/identity/tenants` | Policy: PlatformAdmin | body: CreateTenantRequest | 200: không khai báo schema | [CreateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/TenantEndpoints.cs#L27) |
| GET | `/api/v1/identity/tenants/{id}` | Policy: PlatformAdmin | path: id* string (uuid) | 200: TenantResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/TenantEndpoints.cs#L26) |
| PUT (có thể ghi) | `/api/v1/identity/tenants/{id}` | Policy: PlatformAdmin | path: id* string (uuid); body: UpdateTenantRequest | 200: TenantResponse | [UpdateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/TenantEndpoints.cs#L28) |
| POST (có thể ghi) | `/api/v1/identity/tenants/{id}/approve` | Policy: PlatformAdmin | path: id* string (uuid) | 200: TenantResponse | [ApproveAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L61) |
| POST (có thể ghi) | `/api/v1/identity/tenants/{id}/reject` | Policy: PlatformAdmin | path: id* string (uuid); body: RejectTenantRequest | 200: TenantResponse | [RejectAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/BrandSelfServiceEndpoints.cs#L62) |
| PATCH (có thể ghi) | `/api/v1/identity/tenants/{id}/status` | Policy: PlatformAdmin | path: id* string (uuid); body: ChangeTenantStatusRequest | 200: TenantResponse | [ChangeStatusAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Tenants/TenantEndpoints.cs#L29) |
| GET | `/api/v1/identity/tenants/{tenantId}/subscription` | Policy: PlatformAdmin | path: tenantId* string (uuid) | 200: SubscriptionResponse | [GetCurrentAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L27) |
| POST (có thể ghi) | `/api/v1/identity/tenants/{tenantId}/subscription` | Policy: PlatformAdmin | path: tenantId* string (uuid); body: AssignPlanRequest | 200: không khai báo schema | [AssignAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L28) |
| GET | `/api/v1/identity/tenants/{tenantId}/subscription/payments` | Policy: PlatformAdmin | path: tenantId* string (uuid); query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListPaymentsAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L30) |
| POST (có thể ghi) | `/api/v1/identity/tenants/{tenantId}/subscription/payments` | Policy: PlatformAdmin | path: tenantId* string (uuid); body: RecordPaymentRequest | 200: không khai báo schema | [RecordPaymentAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L29) |
| POST (có thể ghi) | `/api/v1/identity/tenants/{tenantId}/subscription/renew` | Policy: PlatformAdmin | path: tenantId* string (uuid) | 200: SubscriptionResponse | [RenewAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Subscriptions/TenantSubscriptionEndpoints.cs#L31) |
| GET | `/api/v1/identity/users` | Policy: Management | query: search string; query: role string; query: status UserStatus; query: branchId string (uuid); query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L30) |
| POST (có thể ghi) | `/api/v1/identity/users` | Policy: Management | body: CreateUserRequest | 200: không khai báo schema | [CreateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L32) |
| DELETE (có thể ghi) | `/api/v1/identity/users/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L35) |
| GET | `/api/v1/identity/users/{id}` | Policy: Management | path: id* string (uuid) | 200: UserResponse | [GetAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L31) |
| PUT (có thể ghi) | `/api/v1/identity/users/{id}` | Policy: Management | path: id* string (uuid); body: UpdateUserRequest | 200: UserResponse | [UpdateAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L33) |
| PATCH (có thể ghi) | `/api/v1/identity/users/{id}/status` | Policy: Management | path: id* string (uuid); body: ChangeUserStatusRequest | 200: UserResponse | [ChangeStatusAsync](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Users/UserEndpoints.cs#L34) |

## kitchen

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/kitchen/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/kitchen/batches` | Policy: Staff | query: branchId string (uuid) | 200: array<BrewingBatchDto> | [BatchesAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L31) |
| GET | `/api/v1/kitchen/queue` | Policy: Staff | query: branchId string (uuid) | 200: array<KitchenTicketDto> | [QueueAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L30) |
| GET | `/api/v1/kitchen/tickets/{id}` | Policy: Staff | path: id* string (uuid) | 200: KitchenTicketDto | [GetAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L32) |
| POST (có thể ghi) | `/api/v1/kitchen/tickets/{id}/remake` | JWT: Barista OR BranchManager OR BrandAdmin | path: id* string (uuid); body: RemakeTicketRequest | 200: không khai báo schema | [RemakeAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L35) |
| PUT (có thể ghi) | `/api/v1/kitchen/tickets/{id}/status` | Policy: Staff | path: id* string (uuid); body: UpdateKitchenTicketStatusRequest | 200: không khai báo schema | [UpdateStatusAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L33) |
| GET (GET có thể ghi) | `/api/v1/kitchen/tickets/{id}/stickers` | JWT: Barista OR BranchManager OR BrandAdmin | path: id* string (uuid) | 200: object/unspecified | [StickersAsync](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenEndpoints.cs#L34) |

## menu

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/menu/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/menu/branches/{branchId}/menu` | Policy: Staff | path: branchId* string (uuid) | 200: BranchMenuResponse | [GetBranchMenuAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Branches/BranchMenuEndpoints.cs#L42) |
| PATCH (có thể ghi) | `/api/v1/menu/branches/{branchId}/products/{productId}/availability` | Policy: AvailabilityEditors | path: branchId* string (uuid); path: productId* string (uuid); body: ChangeAvailabilityRequest | 200: BranchProductResponse | [ChangeAvailabilityAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Branches/BranchMenuEndpoints.cs#L43) |
| GET | `/api/v1/menu/categories` | Policy: Staff | query: status CategoryStatus | 200: array<CategoryResponse> | [ListAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L30) |
| POST (có thể ghi) | `/api/v1/menu/categories` | Policy: AdminOnly | body: CategoryRequest | 200: không khai báo schema | [CreateAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L32) |
| DELETE (có thể ghi) | `/api/v1/menu/categories/{id}` | Policy: AdminOnly | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L35) |
| GET | `/api/v1/menu/categories/{id}` | Policy: Staff | path: id* string (uuid) | 200: CategoryResponse | [GetAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L31) |
| PUT (có thể ghi) | `/api/v1/menu/categories/{id}` | Policy: AdminOnly | path: id* string (uuid); body: CategoryRequest | 200: CategoryResponse | [UpdateAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L33) |
| PATCH (có thể ghi) | `/api/v1/menu/categories/{id}/status` | Policy: AdminOnly | path: id* string (uuid); body: ChangeCategoryStatusRequest | 200: CategoryResponse | [ChangeStatusAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Categories/CategoryEndpoints.cs#L34) |
| GET | `/api/v1/menu/products` | Policy: Staff | query: categoryId string (uuid); query: search string; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Products/ProductEndpoints.cs#L22) |
| POST (có thể ghi) | `/api/v1/menu/products` | Policy: AdminOnly | body: ProductRequest | 200: không khai báo schema | [CreateAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Products/ProductEndpoints.cs#L24) |
| DELETE (có thể ghi) | `/api/v1/menu/products/{id}` | Policy: AdminOnly | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Products/ProductEndpoints.cs#L26) |
| GET | `/api/v1/menu/products/{id}` | Policy: Staff | path: id* string (uuid) | 200: ProductResponse | [GetAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Products/ProductEndpoints.cs#L23) |
| PUT (có thể ghi) | `/api/v1/menu/products/{id}` | Policy: AdminOnly | path: id* string (uuid); body: ProductRequest | 200: ProductResponse | [UpdateAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Products/ProductEndpoints.cs#L25) |
| GET | `/api/v1/menu/products/{id}/branches` | Policy: Management | path: id* string (uuid) | 200: array<BranchProductResponse> | [ListBranchSettingsAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Branches/BranchMenuEndpoints.cs#L39) |
| PUT (có thể ghi) | `/api/v1/menu/products/{id}/branches/{branchId}` | Policy: AdminOnly | path: id* string (uuid); path: branchId* string (uuid); body: BranchProductRequest | 200: BranchProductResponse | [SetBranchProductAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Branches/BranchMenuEndpoints.cs#L38) |
| GET | `/api/v1/menu/public/branches/{branchId}/menu` | Anonymous | path: branchId* string (uuid) | 200: BranchMenuResponse | [GetMenuAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/PublicMenu/PublicMenuEndpoints.cs#L17) |
| POST (có thể ghi) | `/api/v1/menu/publications` | Policy: AdminOnly | body: PublicationRequest | 200: PublicationResult | [PublishAsync](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Branches/BranchMenuEndpoints.cs#L40) |

## order

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/orders` | Policy: Staff | query: branchId string (uuid); query: status OrderStatus; query: paymentStatus PaymentStatus; query: from string (date); query: to string (date); query: includePaid boolean; query: limit integer (int32); query: offset integer (int32) | 200: array<OrderSummary> | [ListAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L31) |
| POST (có thể ghi) | `/api/v1/orders` | JWT: BrandAdmin OR BranchManager OR Cashier | body: CreateOrderRequest | 200: không khai báo schema | [CreateAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L30) |
| GET | `/api/v1/orders/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/orders/{id}` | JWT | path: id* string (uuid) | 200: OrderResponse | [GetAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L35) |
| POST (có thể ghi) | `/api/v1/orders/{id}/cancel` | JWT: BrandAdmin OR BranchManager OR Cashier OR Customer | path: id* string (uuid); body: CancelOrderRequest | 200: OrderResponse | [CancelAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L39) |
| POST (có thể ghi) | `/api/v1/orders/{id}/deliver` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid) | 200: OrderResponse | [DeliverAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L40) |
| POST (có thể ghi) | `/api/v1/orders/{id}/exception` | Policy: Management | path: id* string (uuid); body: OrderExceptionRequest | 200: OrderResponse | [ExceptionAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L43) |
| GET | `/api/v1/orders/{id}/feedback` | JWT | path: id* string (uuid) | 200: OrderFeedbackResponse | [GetFeedbackAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L28) |
| POST (có thể ghi) | `/api/v1/orders/{id}/feedback` | JWT: Customer | path: id* string (uuid); body: OrderFeedbackRequest | 200: không khai báo schema | [SubmitFeedbackAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L27) |
| POST (có thể ghi) | `/api/v1/orders/{id}/no-show` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid); body: OrderReasonRequest | 200: OrderResponse | [NoShowAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L41) |
| GET | `/api/v1/orders/{id}/payment` | JWT | path: id* string (uuid) | 200: PaymentVerificationResponse | [PaymentAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L36) |
| GET (GET có thể ghi) | `/api/v1/orders/{id}/receipt` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid) | 200: ReceiptResponse | [ReceiptAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L37) |
| PUT (có thể ghi) | `/api/v1/orders/{id}/service-type` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid); body: SwitchServiceTypeRequest | 200: OrderResponse | [SwitchServiceTypeAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L38) |
| POST (có thể ghi) | `/api/v1/orders/{id}/unfulfillable` | JWT: BrandAdmin OR BranchManager OR Cashier OR Barista | path: id* string (uuid); body: OrderReasonRequest | 200: OrderResponse | [UnfulfillableAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L42) |
| GET (GET có thể ghi) | `/api/v1/orders/by-code/{orderCode}/receipt` | JWT: BrandAdmin OR BranchManager OR Cashier | path: orderCode* string | 200: ReceiptResponse | [ReceiptByCodeAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L34) |
| GET | `/api/v1/orders/exceptions` | Policy: Management | query: branchId string (uuid); query: refundStatus RefundStatus; query: from string (date); query: to string (date) | 200: array<OrderSummary> | [ExceptionsAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L33) |
| GET | `/api/v1/orders/feedbacks` | Policy: Management | query: branchId string (uuid); query: minRating integer (int32); query: maxRating integer (int32); query: limit integer (int32) | 200: array<OrderFeedbackResponse> | [ListFeedbacksAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L29) |
| GET | `/api/v1/orders/feedbacks/mine` | JWT: Customer | query: limit integer (int32) | 200: array<OrderFeedbackResponse> | [MyFeedbacksAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L30) |
| GET | `/api/v1/orders/mine` | JWT: Customer | query: limit integer (int32) | 200: array<OrderSummary> | [MineAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs#L32) |
| POST (có thể ghi) | `/api/v1/orders/pickup` | JWT: Customer | body: CreatePickupOrderRequest | 200: không khai báo schema | [CreateAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L25) |
| GET | `/api/v1/orders/pickup-slots` | Policy: Management | - | 200: array<PickupSlotSettingResponse> | [ListAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/PickupSlots/PickupSlotEndpoints.cs#L30) |
| PUT (có thể ghi) | `/api/v1/orders/pickup-slots` | Policy: Management | body: PickupSlotRequest | 200: không khai báo schema | [UpsertAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/PickupSlots/PickupSlotEndpoints.cs#L32) |
| DELETE (có thể ghi) | `/api/v1/orders/pickup-slots/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/PickupSlots/PickupSlotEndpoints.cs#L33) |
| GET | `/api/v1/orders/pickup-slots/available` | JWT | query: branchId string (uuid); query: date string (date) | 200: AvailablePickupSlotsResponse | [AvailableSlotsAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/PickupOrderEndpoints.cs#L24) |
| GET | `/api/v1/orders/pickup-slots/effective` | JWT | query: branchId string (uuid) | 200: EffectivePickupSlots | [EffectiveAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/PickupSlots/PickupSlotEndpoints.cs#L31) |
| DELETE (có thể ghi) | `/api/v1/orders/qr/cart` | X-Table-Session; JWT optional | - | 200: không khai báo schema | [ClearCartAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L77) |
| GET | `/api/v1/orders/qr/cart` | X-Table-Session; JWT optional | - | 200: QrCartView | [GetCartAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L73) |
| POST (có thể ghi) | `/api/v1/orders/qr/cart/items` | X-Table-Session; JWT optional | body: AddQrCartItemRequest | 200: QrCartView | [AddItemAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L74) |
| DELETE (có thể ghi) | `/api/v1/orders/qr/cart/items/{lineId}` | X-Table-Session; JWT optional | path: lineId* string (uuid) | 200: QrCartView | [RemoveItemAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L76) |
| PUT (có thể ghi) | `/api/v1/orders/qr/cart/items/{lineId}` | X-Table-Session; JWT optional | path: lineId* string (uuid); body: SetQrCartQuantityRequest | 200: QrCartView | [SetQuantityAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L75) |
| POST (có thể ghi) | `/api/v1/orders/qr/checkout` | X-Table-Session; JWT optional | body: QrCheckoutRequest | 200: không khai báo schema | [CheckoutAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L78) |
| GET | `/api/v1/orders/qr/orders` | X-Table-Session; JWT optional | - | 200: array<QrOrderStatus> | [OrdersAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L79) |
| POST (có thể ghi) | `/api/v1/orders/qr/scan` | Anonymous | body: ScanQrRequest | 200: không khai báo schema | [ScanAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L72) |
| GET | `/api/v1/orders/qr/session/bill` | X-Table-Session; JWT optional | - | 200: QrBillResponse | [BillAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L80) |
| POST (có thể ghi) | `/api/v1/orders/qr/session/bill-request` | X-Table-Session; JWT optional | body: QrBillRequest | 200: không khai báo schema | [RequestBillAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L81) |
| POST (có thể ghi) | `/api/v1/orders/qr/session/payment-access` | X-Table-Session; JWT optional | - | 200: QrPaymentAccessResponse | [PaymentAccessAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L82) |
| GET | `/api/v1/orders/qr/sessions` | Policy: Staff | query: branchId string (uuid) | 200: array<TableSessionSummary> | [ListSessionsAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L84) |
| GET | `/api/v1/orders/qr/sessions/{id}/bill` | Policy: Staff | path: id* string (uuid) | 200: QrBillResponse | [StaffBillAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L85) |
| POST (có thể ghi) | `/api/v1/orders/qr/sessions/{id}/close` | Policy: Staff | path: id* string (uuid) | 200: không khai báo schema | [CloseSessionAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs#L86) |
| GET | `/api/v1/orders/recommendations` | Anonymous | query: branchId string (uuid); query: limit integer (int32) | 200: RecommendationsResponse | [GetAsync](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/RecommendationEndpoints.cs#L30) |

## payment

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/payments/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/payments/merchant-settings` | Policy: Management | query: branchId string (uuid) | 200: array<MerchantSettingResponse> | [ListAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Merchants/MerchantSettingEndpoints.cs#L43) |
| PUT (có thể ghi) | `/api/v1/payments/merchant-settings` | Policy: AdminOnly | body: MerchantSettingRequest | 200: không khai báo schema | [UpsertAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Merchants/MerchantSettingEndpoints.cs#L45) |
| DELETE (có thể ghi) | `/api/v1/payments/merchant-settings/{id}` | Policy: AdminOnly | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Merchants/MerchantSettingEndpoints.cs#L46) |
| GET | `/api/v1/payments/merchant-settings/available` | JWT | query: branchId string (uuid) | 200: array<AvailablePaymentMethod> | [AvailableAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Merchants/MerchantSettingEndpoints.cs#L44) |
| GET | `/api/v1/payments/policies/cancellation` | JWT | - | 200: CancellationPolicyResponse | [GetAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Policies/CancellationPolicyEndpoints.cs#L43) |
| PUT (có thể ghi) | `/api/v1/payments/policies/cancellation` | Policy: AdminOnly | body: CancellationPolicyRequest | 200: CancellationPolicyResponse | [UpdateAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Policies/CancellationPolicyEndpoints.cs#L44) |
| GET | `/api/v1/payments/providers` | Policy: PlatformAdmin | query: isEnabled boolean | 200: array<ProviderResponse> | [ListAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L59) |
| POST (có thể ghi) | `/api/v1/payments/providers` | Policy: PlatformAdmin | body: CreateProviderRequest | 200: không khai báo schema | [CreateAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L61) |
| GET | `/api/v1/payments/providers/{id}` | Policy: PlatformAdmin | path: id* string (uuid) | 200: ProviderResponse | [GetAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L60) |
| PUT (có thể ghi) | `/api/v1/payments/providers/{id}` | Policy: PlatformAdmin | path: id* string (uuid); body: UpdateProviderRequest | 200: ProviderResponse | [UpdateAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L62) |
| PATCH (có thể ghi) | `/api/v1/payments/providers/{id}/status` | Policy: PlatformAdmin | path: id* string (uuid); body: ProviderStatusRequest | 200: ProviderResponse | [ChangeStatusAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L63) |
| GET | `/api/v1/payments/providers/enabled` | JWT | - | 200: array<EnabledProviderResponse> | [ListEnabledAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Providers/ProviderEndpoints.cs#L57) |
| GET | `/api/v1/payments/reconciliation` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: ReconciliationReport | [ReportAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/ReconciliationEndpoints.cs#L51) |
| GET | `/api/v1/payments/reconciliation/auto` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: OrderReconciliationReport | [AutoAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/ReconciliationEndpoints.cs#L53) |
| POST (có thể ghi) | `/api/v1/payments/reconciliation/orders` | Policy: Management | body: OrderReconciliationRequest | 200: OrderReconciliationReport | [CompareOrdersAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/ReconciliationEndpoints.cs#L52) |
| GET | `/api/v1/payments/refunds` | JWT: BrandAdmin OR BranchManager OR Cashier | query: status RefundStatus; query: branchId string (uuid); query: paymentId string (uuid); query: limit integer (int32) | 200: array<RefundResponse> | [ListAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/RefundEndpoints.cs#L32) |
| GET | `/api/v1/payments/refunds/{id}` | JWT: BrandAdmin OR BranchManager OR Cashier | path: id* string (uuid) | 200: RefundResponse | [GetAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/RefundEndpoints.cs#L33) |
| POST (có thể ghi) | `/api/v1/payments/refunds/{id}/approve` | Policy: Management | path: id* string (uuid) | 200: RefundResponse | [ApproveAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/RefundEndpoints.cs#L34) |
| POST (có thể ghi) | `/api/v1/payments/refunds/{id}/reject` | Policy: Management | path: id* string (uuid); body: RefundDecisionRequest | 200: RefundResponse | [RejectAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/RefundEndpoints.cs#L35) |
| GET | `/api/v1/payments/transactions` | JWT | query: orderId string (uuid); query: branchId string (uuid); query: status PaymentStatus; query: limit integer (int32) | 200: array<PaymentResponse> | [ListAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L40) |
| POST (có thể ghi) | `/api/v1/payments/transactions` | JWT | body: CreatePaymentRequest | 200: không khai báo schema | [CreateAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L38) |
| GET (GET có thể ghi) | `/api/v1/payments/transactions/{id}` | JWT | path: id* string (uuid) | 200: PaymentResponse | [GetAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L41) |
| POST (có thể ghi) | `/api/v1/payments/transactions/{id}/cancel` | JWT | path: id* string (uuid) | 200: PaymentResponse | [CancelAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L42) |
| POST (có thể ghi) | `/api/v1/payments/transactions/{id}/query` | JWT | path: id* string (uuid) | 200: PaymentResponse | [QueryAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L43) |
| POST (có thể ghi) | `/api/v1/payments/transactions/{id}/refunds` | JWT: BrandAdmin OR BranchManager OR Cashier OR Customer OR Barista | path: id* string (uuid); body: RefundRequest | 200: không khai báo schema | [RequestAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/RefundEndpoints.cs#L29) |
| POST (có thể ghi) | `/api/v1/payments/transactions/{id}/simulate` | JWT | path: id* string (uuid); body: SimulatePaymentRequest | 200: PaymentResponse | [SimulateAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L44) |
| POST (có thể ghi) | `/api/v1/payments/transactions/cash` | JWT: BrandAdmin OR BranchManager OR Cashier | body: CashPaymentRequest | 200: không khai báo schema | [RecordCashAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs#L39) |
| POST (có thể ghi) | `/api/v1/payments/webhooks/{provider}` | HMAC signature; không cần JWT | path: provider* string; body: WebhookRequest | 200: WebhookResponse | [ReceiveAsync](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/WebhookEndpoints.cs#L25) |

## reporting

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/reporting/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/reporting/branch` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: BranchReport | [BranchAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L52) |
| GET | `/api/v1/reporting/branch/export` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: không khai báo schema | [BranchExportAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L53) |
| GET | `/api/v1/reporting/chain` | Policy: Management AND Policy: AdminOnly | query: from string (date); query: to string (date) | 200: ChainReport | [ChainAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L50) |
| GET | `/api/v1/reporting/chain/export` | Policy: Management AND Policy: AdminOnly | query: from string (date); query: to string (date) | 200: không khai báo schema | [ChainExportAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L51) |
| GET | `/api/v1/reporting/payment-methods` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: array<MethodRevenue> | [PaymentMethodsAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L54) |
| GET | `/api/v1/reporting/peak-hours` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: array<HourCount> | [PeakHoursAsync](../src/Services/ReportingAnalytics/AICOS.ReportingAnalytics.Api/Features/Reports/ReportEndpoints.cs#L55) |

## table

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/tables/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| GET | `/api/v1/tables/areas` | Policy: Staff | query: branchId string (uuid) | 200: array<AreaResponse> | [ListAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/Areas/AreaEndpoints.cs#L30) |
| POST (có thể ghi) | `/api/v1/tables/areas` | Policy: Management | body: CreateAreaRequest | 200: không khai báo schema | [CreateAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/Areas/AreaEndpoints.cs#L32) |
| DELETE (có thể ghi) | `/api/v1/tables/areas/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/Areas/AreaEndpoints.cs#L34) |
| GET | `/api/v1/tables/areas/{id}` | Policy: Staff | path: id* string (uuid) | 200: AreaResponse | [GetAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/Areas/AreaEndpoints.cs#L31) |
| PUT (có thể ghi) | `/api/v1/tables/areas/{id}` | Policy: Management | path: id* string (uuid); body: UpdateAreaRequest | 200: AreaResponse | [UpdateAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/Areas/AreaEndpoints.cs#L33) |
| GET | `/api/v1/tables/dining-tables` | Policy: Staff | query: branchId string (uuid); query: areaId string (uuid); query: status TableStatus | 200: array<DiningTableResponse> | [ListAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L49) |
| POST (có thể ghi) | `/api/v1/tables/dining-tables` | Policy: Management | body: DiningTableRequest | 200: không khai báo schema | [CreateAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L51) |
| DELETE (có thể ghi) | `/api/v1/tables/dining-tables/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [DeleteAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L56) |
| GET | `/api/v1/tables/dining-tables/{id}` | Policy: Staff | path: id* string (uuid) | 200: DiningTableResponse | [GetAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L50) |
| PUT (có thể ghi) | `/api/v1/tables/dining-tables/{id}` | Policy: Management | path: id* string (uuid); body: DiningTableRequest | 200: DiningTableResponse | [UpdateAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L52) |
| GET | `/api/v1/tables/dining-tables/{id}/qr.svg` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [QrImageAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L55) |
| POST (có thể ghi) | `/api/v1/tables/dining-tables/{id}/qr/regenerate` | Policy: Management | path: id* string (uuid) | 200: DiningTableResponse | [RegenerateQrAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L54) |
| PATCH (có thể ghi) | `/api/v1/tables/dining-tables/{id}/status` | Policy: Staff | path: id* string (uuid); body: ChangeTableStatusRequest | 200: DiningTableResponse | [ChangeStatusAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L53) |
| POST (có thể ghi) | `/api/v1/tables/dining-tables/batch` | Policy: Management | body: TableBatchRequest | 200: không khai báo schema | [CreateBatchAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/TableBatchEndpoints.cs#L43) |
| GET | `/api/v1/tables/floor-plan` | Policy: Staff | query: branchId string (uuid) | 200: FloorPlanResponse | [FloorPlanAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L58) |
| POST (có thể ghi) | `/api/v1/tables/floor-plan/clone` | Policy: AdminOnly | body: FloorPlanCloneRequest | 200: không khai báo schema | [CloneAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/TableBatchEndpoints.cs#L45) |
| PUT (có thể ghi) | `/api/v1/tables/floor-plan/layout` | Policy: Management | body: TableLayoutRequest | 200: TableLayoutResponse | [SaveLayoutAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/TableBatchEndpoints.cs#L44) |
| GET | `/api/v1/tables/public/qr/{token}` | Anonymous | path: token* string | 200: QrLookupResponse | [LookupQrAsync](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/DiningTables/DiningTableEndpoints.cs#L59) |

## workforce

| Method | Path qua Gateway | Quyền route/group | Input theo OpenAPI | Output theo OpenAPI | Source/handler |
| --- | --- | --- | --- | --- | --- |
| GET | `/api/v1/workforce/_info` | Anonymous | - | 200: 0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] | [MapAicosApiGroup / info](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/ServiceDefaultsExtensions.cs) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/{id}/absent` | Policy: Management | path: id* string (uuid); body: MarkAbsentRequest | 200: ShiftAssignmentResponse | [AbsentAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L88) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/{id}/adjust` | Policy: Management | path: id* string (uuid); body: AdjustAttendanceRequest | 200: ShiftAssignmentResponse | [AdjustAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L87) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/{id}/approve` | Policy: Management | path: id* string (uuid); body: ApproveAttendanceRequest | 200: ShiftAssignmentResponse | [ApproveAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L86) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/approve-range` | Policy: Management | body: ApproveRangeRequest | 200: ApproveRangeResult | [ApproveRangeAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L85) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/check-in` | Policy: Staff | body: AttendanceRequest | 200: ShiftAssignmentResponse | [CheckInAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L81) |
| POST (có thể ghi) | `/api/v1/workforce/attendance/check-out` | Policy: Staff | body: AttendanceRequest | 200: ShiftAssignmentResponse | [CheckOutAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L82) |
| GET | `/api/v1/workforce/attendance/report` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date); query: employeeId string (uuid); query: reviewStatus AttendanceReviewStatus | 200: AttendanceReportResponse | [ReportAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Attendance/AttendanceEndpoints.cs#L84) |
| GET | `/api/v1/workforce/cash-closings` | Policy: Management | query: branchId string (uuid); query: status CashClosingStatus; query: withDifference boolean; query: from string (date); query: to string (date); query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L69) |
| POST (có thể ghi) | `/api/v1/workforce/cash-closings` | Policy: Staff | body: SubmitCashClosingRequest | 200: không khai báo schema | [SubmitAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L66) |
| GET | `/api/v1/workforce/cash-closings/{id}` | Policy: Staff | path: id* string (uuid) | 200: CashClosingResponse | [GetAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L70) |
| POST (có thể ghi) | `/api/v1/workforce/cash-closings/{id}/confirm` | Policy: Management | path: id* string (uuid); body: ConfirmCashClosingRequest | 200: CashClosingResponse | [ConfirmAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L72) |
| GET | `/api/v1/workforce/cash-closings/{id}/details` | Policy: Management | path: id* string (uuid) | 200: CashClosingDetailsResponse | [DetailsAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L71) |
| GET | `/api/v1/workforce/cash-closings/expected` | Policy: Staff | query: assignmentId string (uuid) | 200: ExpectedCashResponse | [ExpectedAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L67) |
| GET | `/api/v1/workforce/cash-closings/mine` | Policy: Staff | - | 200: array<CashClosingResponse> | [MineAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/CashClosing/CashClosingEndpoints.cs#L68) |
| GET | `/api/v1/workforce/employees` | Policy: Management | query: branchId string (uuid); query: search string; query: status EmployeeStatus; query: contractType ContractType; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L50) |
| POST (có thể ghi) | `/api/v1/workforce/employees` | Policy: Management | body: EmployeeRequest | 200: không khai báo schema | [CreateAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L53) |
| GET | `/api/v1/workforce/employees/{id}` | Policy: Management | path: id* string (uuid) | 200: EmployeeResponse | [GetAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L52) |
| PUT (có thể ghi) | `/api/v1/workforce/employees/{id}` | Policy: Management | path: id* string (uuid); body: UpdateEmployeeRequest | 200: EmployeeResponse | [UpdateAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L54) |
| PATCH (có thể ghi) | `/api/v1/workforce/employees/{id}/status` | Policy: Management | path: id* string (uuid); body: ChangeEmployeeStatusRequest | 200: EmployeeResponse | [ChangeStatusAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L55) |
| GET | `/api/v1/workforce/employees/me` | Policy: Staff | - | 200: EmployeeResponse | [MeAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L51) |
| GET | `/api/v1/workforce/payroll/adjustments` | Policy: Management | query: branchId string (uuid); query: employeeId string (uuid); query: from string (date); query: to string (date) | 200: array<PayrollAdjustmentResponse> | [ListAdjustmentsAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L139) |
| POST (có thể ghi) | `/api/v1/workforce/payroll/adjustments` | Policy: Management | body: PayrollAdjustmentRequest | 200: không khai báo schema | [AddAdjustmentAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L140) |
| DELETE (có thể ghi) | `/api/v1/workforce/payroll/adjustments/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [RemoveAdjustmentAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L141) |
| GET | `/api/v1/workforce/payroll/export` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date); query: format string | 200: không khai báo schema | [ExportAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L137) |
| GET | `/api/v1/workforce/payroll/records/export` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date); query: format string | 200: không khai báo schema | [ExportRecordsAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L138) |
| GET | `/api/v1/workforce/payroll/summary` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date) | 200: PayrollSummaryResponse | [SummaryAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L136) |
| GET | `/api/v1/workforce/pay-rules` | Policy: Management | - | 200: PayRuleResponse | [GetRulesAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L132) |
| PUT (có thể ghi) | `/api/v1/workforce/pay-rules` | Policy: AdminOnly | body: PayRuleRequest | 200: PayRuleResponse | [UpdateRulesAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Payroll/PayrollEndpoints.cs#L133) |
| GET | `/api/v1/workforce/replacement-shifts` | Policy: Staff | query: branchId string (uuid); query: status ReplacementStatus; query: from string (date); query: to string (date) | 200: array<ReplacementShiftResponse> | [ListAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L63) |
| POST (có thể ghi) | `/api/v1/workforce/replacement-shifts` | Policy: Management | body: OpenReplacementRequest | 200: không khai báo schema | [OpenAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L65) |
| GET | `/api/v1/workforce/replacement-shifts/{id}` | Policy: Staff | path: id* string (uuid) | 200: ReplacementShiftResponse | [GetAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L64) |
| GET | `/api/v1/workforce/replacement-shifts/{id}/applications` | Policy: Management | path: id* string (uuid) | 200: array<ReplacementApplicationResponse> | [ListApplicationsAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L66) |
| POST (có thể ghi) | `/api/v1/workforce/replacement-shifts/{id}/approve` | Policy: Management | path: id* string (uuid); body: ApproveReplacementRequest | 200: ReplacementShiftResponse | [ApproveAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L67) |
| POST (có thể ghi) | `/api/v1/workforce/replacement-shifts/{id}/cancel` | Policy: Management | path: id* string (uuid); body: CancelReplacementRequest | 200: ReplacementShiftResponse | [CancelAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L68) |
| POST (có thể ghi) | `/api/v1/workforce/replacement-shifts/{id}/register` | Policy: Staff | path: id* string (uuid); body: RegisterReplacementRequest | 200: ReplacementShiftResponse | [RegisterAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L69) |
| POST (có thể ghi) | `/api/v1/workforce/replacement-shifts/{id}/withdraw` | Policy: Staff | path: id* string (uuid) | 200: ReplacementShiftResponse | [WithdrawAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Replacements/ReplacementEndpoints.cs#L70) |
| GET | `/api/v1/workforce/shift-assignments` | Policy: Management | query: branchId string (uuid); query: from string (date); query: to string (date); query: employeeId string (uuid); query: status AssignmentStatus; query: page integer (int32); query: pageSize integer (int32) | 200: 0, Culture=neutral, PublicKeyToken=null]] | [ListAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L52) |
| POST (có thể ghi) | `/api/v1/workforce/shift-assignments` | Policy: Management | body: AssignShiftRequest | 200: không khai báo schema | [AssignAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L55) |
| DELETE (có thể ghi) | `/api/v1/workforce/shift-assignments/{id}` | Policy: Management | path: id* string (uuid) | 200: không khai báo schema | [RemoveAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L56) |
| GET | `/api/v1/workforce/shift-assignments/{id}` | Policy: Staff | path: id* string (uuid) | 200: ShiftAssignmentResponse | [GetAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L54) |
| POST (có thể ghi) | `/api/v1/workforce/shift-assignments/{id}/confirm` | Policy: Staff | path: id* string (uuid) | 200: ShiftAssignmentResponse | [ConfirmAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L57) |
| POST (có thể ghi) | `/api/v1/workforce/shift-assignments/{id}/decline` | Policy: Staff | path: id* string (uuid); body: DeclineShiftRequest | 200: ShiftAssignmentResponse | [DeclineAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L58) |
| GET | `/api/v1/workforce/shift-assignments/mine` | Policy: Staff | query: from string (date); query: to string (date) | 200: array<ShiftAssignmentResponse> | [MineAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Scheduling/ScheduleEndpoints.cs#L53) |
| GET | `/api/v1/workforce/work-shifts` | Policy: Staff | query: branchId string (uuid); query: activeOnly boolean | 200: array<WorkShiftResponse> | [ListShiftsAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L58) |
| POST (có thể ghi) | `/api/v1/workforce/work-shifts` | Policy: Management | body: WorkShiftRequest | 200: không khai báo schema | [CreateShiftAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L60) |
| GET | `/api/v1/workforce/work-shifts/{id}` | Policy: Staff | path: id* string (uuid) | 200: WorkShiftResponse | [GetShiftAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L59) |
| PUT (có thể ghi) | `/api/v1/workforce/work-shifts/{id}` | Policy: Management | path: id* string (uuid); body: WorkShiftRequest | 200: WorkShiftResponse | [UpdateShiftAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L61) |
| PATCH (có thể ghi) | `/api/v1/workforce/work-shifts/{id}/status` | Policy: Management | path: id* string (uuid); body: ChangeWorkShiftStatusRequest | 200: WorkShiftResponse | [ChangeShiftStatusAsync](../src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/Employees/EmployeeEndpoints.cs#L62) |

## JSON schema tham khảo cho Mobile

Các bảng sau trích từ OpenAPI; required/nullable là mô tả schema, không thay thế validator. Một số field có thể bị ẩn khỏi OpenAPI khi endpoint trả IResult; không tự suy ra DTO chưa có. Xem handler/record ở source nếu thiếu.

### identity — AuthResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| accessToken | string | False | True |
| accessTokenExpiresAtUtc | string (date-time) | False | False |
| refreshToken | string | False | True |
| refreshTokenExpiresAtUtc | string (date-time) | False | False |
| user | UserResponse | False | False |
| activeBrandCode | string | False | True |
| tokenType | string | False | True |

### identity — LoginRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| login | string | False | True |
| password | string | False | True |

### identity — UserResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| id | string (uuid) | False | False |
| fullName | string | False | True |
| email | string | False | True |
| username | string | False | True |
| phone | string | False | True |
| role | string | False | True |
| tenantId | string (uuid) | False | True |
| branchId | string (uuid) | False | True |
| status | UserStatus | False | False |
| mustChangePassword | boolean | False | False |
| lockoutEndUtc | string (date-time) | False | True |
| lastLoginAtUtc | string (date-time) | False | True |
| createdAtUtc | string (date-time) | False | False |
| updatedAtUtc | string (date-time) | False | True |

### kitchen — UpdateKitchenTicketStatusRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| status | KitchenTicketStatus | False | False |

### order — CreateOrderRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| branchId | string (uuid) | False | True |
| type | OrderType | False | False |
| customerId | string (uuid) | False | True |
| tableId | string (uuid) | False | True |
| tableNumber | string | False | True |
| deliveryAddress | string | False | True |
| paymentMethod | PaymentMethod | False | False |
| promotionCode | string | False | True |
| note | string | False | True |
| items | array<OrderItemRequest> | False | True |

### order — OrderItemRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| productId | string (uuid) | False | True |
| quantity | integer (int32) | False | True |
| note | string | False | True |
| optionIds | array<string (uuid)> | False | True |

### order — OrderResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| id | string (uuid) | False | False |
| orderCode | string | False | True |
| branchId | string (uuid) | False | False |
| cashierId | string (uuid) | False | True |
| customerId | string (uuid) | False | True |
| type | OrderType | False | False |
| status | OrderStatus | False | False |
| paymentStatus | PaymentStatus | False | False |
| paymentMethod | PaymentMethod | False | False |
| paymentProvider | string | False | True |
| paymentId | string (uuid) | False | True |
| tableId | string (uuid) | False | True |
| tableNumber | string | False | True |
| deliveryAddress | string | False | True |
| note | string | False | True |
| originalAmount | number (double) | False | False |
| promotionCode | string | False | True |
| discountAmount | number (double) | False | False |
| finalAmount | number (double) | False | False |
| refundedAmount | number (double) | False | False |
| refundStatus | RefundStatus | False | False |
| createdAtUtc | string (date-time) | False | False |
| paidAtUtc | string (date-time) | False | True |
| readyAtUtc | string (date-time) | False | True |
| deliveredAtUtc | string (date-time) | False | True |
| completedAtUtc | string (date-time) | False | True |
| cancelledAtUtc | string (date-time) | False | True |
| cancelledReason | string | False | True |
| items | array<OrderItemResponse> | False | True |
| pickupSlotStartUtc | string (date-time) | False | True |
| paymentDueAtUtc | string (date-time) | False | True |
| tableSessionId | string (uuid) | False | True |
| remakeCount | integer (int32) | False | False |
| hasFeedback | boolean | False | False |

### order — QrBillResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| sessionId | string (uuid) | False | False |
| tableCode | string | False | True |
| status | TableSessionStatus | False | False |
| orders | array<QrBillOrder> | False | True |
| total | number (double) | False | False |
| paid | number (double) | False | False |
| due | number (double) | False | False |

### order — ScanQrRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| qrToken | string | False | True |

### order — TableSessionSummary

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| id | string (uuid) | False | False |
| branchId | string (uuid) | False | False |
| tableId | string (uuid) | False | False |
| tableCode | string | False | True |
| status | TableSessionStatus | False | False |
| openedAtUtc | string (date-time) | False | False |
| lastActivityAtUtc | string (date-time) | False | False |
| orders | integer (int32) | False | False |
| due | number (double) | False | False |
| billRequestedAtUtc | string (date-time) | False | True |
| billRequestedMethod | string | False | True |

### payment — CashPaymentRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| orderId | string (uuid) | False | True |
| branchId | string (uuid) | False | True |
| amount | number (double) | False | True |
| amountTendered | number (double) | False | True |

### payment — CreatePaymentRequest

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| orderId | string (uuid) | False | True |
| branchId | string (uuid) | False | True |
| amount | number (double) | False | True |
| providerCode | string | False | True |
| description | string | False | True |

### payment — PaymentResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| id | string (uuid) | False | False |
| branchId | string (uuid) | False | False |
| orderId | string (uuid) | False | False |
| amount | number (double) | False | False |
| method | string | False | True |
| status | PaymentStatus | False | False |
| transactionRef | string | False | True |
| checkoutUrl | string | False | True |
| expiresAtUtc | string (date-time) | False | True |
| paidAtUtc | string (date-time) | False | True |
| failureReason | string | False | True |
| paidLate | boolean | False | False |
| refundedAmount | number (double) | False | False |
| refundable | number (double) | False | False |
| createdAtUtc | string (date-time) | False | False |
| refunds | array<RefundResponse> | False | True |

### table — DiningTableResponse

| Field JSON | Type | Required trong schema | Nullable |
| --- | --- | --- | --- |
| id | string (uuid) | False | False |
| branchId | string (uuid) | False | False |
| areaId | string (uuid) | False | False |
| areaName | string | False | True |
| tableCode | string | False | True |
| capacity | integer (int32) | False | False |
| positionX | integer (int32) | False | True |
| positionY | integer (int32) | False | True |
| status | TableStatus | False | False |
| qrToken | string | False | True |
| qrCodeUrl | string | False | True |
| qrIssuedAtUtc | string (date-time) | False | False |

## Ngoài danh mục OpenAPI hiện chạy

- Gateway: GET / (public), GET /health/live (public), GET /api/v1/platform/health (PlatformAdmin/SuperAdmin). YARP proxy 10 prefix service và 2 hub; không có Swagger tổng hợp.
- SignalR /hubs/kitchen: JWT + JoinKitchenGroup/JoinPosGroup có kiểm tra role/tenant/branch. /hubs/orders: guest sessionToken hoặc JWT Customer và kiểm tra group ownership. Transport/negotiate không nằm trong 268 operation trên; chưa test WebSocket trong audit.
- DevTokenEndpoints chỉ map trong Development; runtime Production không có. Không dùng endpoint dev token thay cho login thật.
- ai-models/service/app.py là FastAPI standalone khác, có POST /api/v1/ai/chat, /chat/stream, /feedback/analyze, /recommendations và GET /models. Source có xử lý model nhưng chưa nằm trong Gateway/runtime hiện tại. Chưa chạy service/model nặng; không tính các route này là đã deploy.

## Kiểm tra danh mục

- 268/268 operation có route/handler source tương ứng; quyền group được cộng với quyền endpoint. Role arrays CounterRoles/BarRoles được mở theo source.
- Không dùng security global của Swagger làm bằng chứng mọi endpoint cần JWT: source có AllowAnonymous và capability/HMAC riêng.
- Đã đối chiếu riêng auth/session, QR, order/receipt, Kitchen/status/stickers, Payment/cash/webhook/gateway, shared policies và các nguồn side effect quan trọng. Chưa chứng minh mọi nhánh handler bằng runtime test.
- Không chạy migration/seed, không gọi API ghi, không sửa implementation hoặc restart container trong audit này.
