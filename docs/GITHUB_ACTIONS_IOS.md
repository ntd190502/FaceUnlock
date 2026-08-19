# Build FaceUnlock IPA bằng GitHub Actions

Workflow: `.github/workflows/build-ios-ipa.yml`

Workflow compile và đóng gói `FaceUnlock.ipa` trên macOS runner bằng XcodeGen. Luồng online hiện tại dùng **Telegram + foreground polling**, không dùng APNs/PushManager.

## Chế độ 1 — unsigned-jb

Dành cho môi trường jailbreak/TrollStore phù hợp. Không cần Apple certificate hoặc provisioning profile trong workflow.

GitHub dùng macOS runner + Xcode để compile app cho thiết bị iOS, sau đó đóng gói `Payload/FaceUnlock.app` thành `FaceUnlock.ipa`.

## Chế độ 2 — signed-development

Cần Apple Development certificate `.p12` và development provisioning profile `.mobileprovision` có App ID phù hợp.

Tạo 4 repository secrets:

- `BUILD_CERTIFICATE_BASE64` — file `.p12` chuyển sang Base64.
- `P12_PASSWORD` — mật khẩu file `.p12`.
- `BUILD_PROVISION_PROFILE_BASE64` — file `.mobileprovision` chuyển sang Base64.
- `KEYCHAIN_PASSWORD` — mật khẩu ngẫu nhiên cho keychain tạm của runner.

Ví dụ PowerShell:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("apple-development.p12")) | Set-Content cert_base64.txt
[Convert]::ToBase64String([IO.File]::ReadAllBytes("FaceUnlock.mobileprovision")) | Set-Content profile_base64.txt
```

## Cách chạy

1. Đảm bảo `.github/workflows/build-ios-ipa.yml` nằm trên nhánh mặc định.
2. Vào **Actions** → **Build FaceUnlock iOS IPA**.
3. Chọn **Run workflow**.
4. Chọn `unsigned-jb` hoặc `signed-development`.
5. Kiểm tra `bundle_id`; signed mode phải khớp provisioning profile.
6. Chờ build xong và tải artifact `FaceUnlock-...-IPA`.
7. Artifact chứa `FaceUnlock.ipa` và SHA-256.

## Bundle ID

Bundle ID mặc định là `io.faceunlock.app`. Hosting Telegram không phụ thuộc Bundle ID; Bundle ID chỉ cần nhất quán với cấu hình/signing iOS và URL scheme của app.

## Kiểm tra thêm

Workflow `.github/workflows/ci.yml` có self-test riêng cho BLE framing Swift. Nó kiểm tra chia gói/reassembly, duplicate chunk, out-of-order chunk, giới hạn kích thước và backward compatibility trước khi coi phase BLE hoàn tất.

## Khi workflow lỗi

- `xcodebuild`/Swift compile error: kiểm tra source/Xcode SDK.
- Provisioning profile mismatch: Bundle ID/profile không khớp.
- No code-signing identity: P12/password/certificate không đúng.
- BLE framing self-test fail: không phát hành IPA cho tới khi codec Windows/iOS đồng bộ trở lại.
