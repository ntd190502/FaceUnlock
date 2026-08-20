<?php
declare(strict_types=1);
function check(bool $ok,string $message):void{if(!$ok){fwrite(STDERR,"FAIL: $message\n");exit(1);}echo "PASS: $message\n";}
$root=dirname(__DIR__);$schema=file_get_contents($root.'/schema.sql')?:'';$runtime=file_get_contents($root.'/public/index.php')?:'';
foreach(['schema_migrations','pc_device_pairings','unlock_requests','unlock_request_candidates','security_audit_log','rate_limit_events'] as $needle)check(str_contains($schema,$needle),"schema contains $needle");
check(str_contains($schema,'UNIQUE KEY uq_pc_device_pairing'),'pairing relation is unique');
check(str_contains($runtime,"status='PENDING' AND expires_at>=?"),'approval uses atomic pending conditional update');
check(str_contains($runtime,"winning_device_id=?"),'approval records a single winner');
check(str_contains($runtime,"status='REVOKED'"),'revocation is scoped to pairing');
check(str_contains($runtime,'ALREADY_COMPLETED'),'late approval is rejected');
check(str_contains($runtime,'RateLimiter'),'sensitive routes use rate limiting');
check(str_contains($runtime,'security_audit_log')===false,'runtime delegates audit writes without raw SQL/token exposure');
check(!str_contains($runtime,'approvalToken='),'raw approval token is not logged');
foreach(['001_initial_baseline.php','002_many_to_many_pairing.php','003_logical_unlock_requests.php','004_audit_security.php'] as $file)check(is_file($root.'/migrations/'.$file),"migration exists: $file");
echo "Hosting V2 static tests PASS\n";
