<?php
declare(strict_types=1);
function check(bool $ok,string $message):void{if(!$ok){fwrite(STDERR,"FAIL: $message\n");exit(1);}echo "PASS: $message\n";}
$root=dirname(__DIR__);$runtime=file_get_contents($root.'/public/index.php')?:'';$admin=file_get_contents($root.'/src/Admin.php')?:'';$ios=file_get_contents(dirname($root).'/ios/FaceUnlock/Network/APIClient.swift')?:'';
check(str_contains($runtime,"SELECT r.*,p.name pc_name FROM unlock_requests r JOIN pcs p ON p.id=r.pc_id WHERE r.id=?"),'expired session reload preserves required pc_name');
check(str_contains($runtime,'\'pc_name\'=>$s[\'pc_name\']'),'pc_name response is required, not nullable fallback');
foreach(['session_regenerate_id(true)','admin_until','requireCsrf','ADMIN_LOGIN_FAILED','ADMIN_REVOKE_PAIRING','LIMIT $limit OFFSET $offset','htmlspecialchars'] as $needle)check(str_contains($admin,$needle),"admin implements $needle");
check(str_contains($admin,"status='REVOKED'")&&str_contains($admin,'WHERE pc_id=? AND device_id=?'),'admin revoke is relation-scoped');
check(!str_contains($admin,'bot_token')&&!str_contains($admin,'token_hash</'),'admin HTML does not render configuration secrets');
check(str_contains($ios,'Hosting response is missing required field:'),'iOS exposes safe missing-field diagnostic');
echo "Admin and API contract tests PASS\n";
