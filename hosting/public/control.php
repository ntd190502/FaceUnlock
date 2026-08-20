<?php
declare(strict_types=1);
$configPath=dirname(__DIR__).'/config.php'; if(!is_file($configPath)){http_response_code(500);exit('Missing hosting/config.php');}$config=require $configPath;
foreach(['Util','Database','Auth','AuditLog','RateLimiter'] as $f) require dirname(__DIR__).'/src/'.$f.'.php';
$db=new Database($config['db']); $auth=new Auth($db); $audit=new AuditLog($db); $rate=new RateLimiter($db,$audit,$config['rate_limits']??[]);
$method=$_SERVER['REQUEST_METHOD']; $path=parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH)?:'/'; if(($i=strpos($path,'/v1/control/'))!==false)$path=substr($path,$i);
function pairing(Database $db,string $pc,string $dev):bool{return (bool)$db->one("SELECT id FROM pc_device_pairings WHERE pc_id=? AND device_id=? AND status='ACTIVE'",[$pc,$dev]);}
function cleanCommands(Database $db):void{$db->exec("UPDATE remote_commands SET status='EXPIRED',completed_at=NOW() WHERE status IN ('PENDING','RUNNING') AND expires_at<?",[time()]);}
cleanCommands($db);
$allowed=['status','lock','restart','shutdown','screenshot','apps','close_app','clipboard_get','clipboard_set','file_upload','clipboard_file_download'];
if($method==='POST'&&$path==='/v1/control/command'){
 $dev=$auth->device(); $b=Util::jsonBody(); $pc=(string)($b['pc_id']??''); $type=(string)($b['type']??'');
 if(!$pc||!in_array($type,$allowed,true))Util::error('INVALID_COMMAND',400); if(!pairing($db,$pc,$dev['id']))Util::error('PAIRING_NOT_FOUND',404);
 $payload=$b['payload']??new stdClass(); $encoded=json_encode($payload,JSON_UNESCAPED_SLASHES); if($encoded===false||strlen($encoded)>12*1024*1024)Util::error('PAYLOAD_TOO_LARGE',413);
 $id=Util::id(); $ttl=in_array($type,['file_upload','clipboard_file_download','screenshot'],true)?180:45;
 $db->exec("INSERT INTO remote_commands(id,pc_id,device_id,command_type,payload,expires_at)VALUES(?,?,?,?,?,?)",[$id,$pc,$dev['id'],$type,$encoded,time()+$ttl]);
 $audit->write('REMOTE_COMMAND','QUEUED',$pc,$dev['id'],$id,['type'=>$type]); Util::out(['ok'=>true,'command_id'=>$id,'status'=>'PENDING','expires_at'=>time()+$ttl]);
}
if($method==='GET'&&preg_match('#^/v1/control/result/([^/]+)$#',$path,$m)){
 $dev=$auth->device(); $r=$db->one('SELECT id,pc_id,command_type,status,result,created_at,completed_at FROM remote_commands WHERE id=? AND device_id=?',[$m[1],$dev['id']]); if(!$r)Util::error('NOT_FOUND',404);
 $result=$r['result']?json_decode($r['result'],true):null; Util::out(['ok'=>true,'command_id'=>$r['id'],'type'=>$r['command_type'],'status'=>$r['status'],'result'=>$result]);
}
if($method==='GET'&&$path==='/v1/control/pending'){
 $pc=$auth->pc(); $r=$db->transaction(function()use($db,$pc){$r=$db->one("SELECT * FROM remote_commands WHERE pc_id=? AND status='PENDING' AND expires_at>=? ORDER BY created_at LIMIT 1 FOR UPDATE",[$pc['id'],time()]); if($r)$db->exec("UPDATE remote_commands SET status='RUNNING',claimed_at=NOW() WHERE id=? AND status='PENDING'",[$r['id']]); return $r;});
 Util::out(['ok'=>true,'pending'=>(bool)$r,'command'=>$r?['id'=>$r['id'],'type'=>$r['command_type'],'payload'=>$r['payload']?json_decode($r['payload'],true):null]:null]);
}
if($method==='POST'&&preg_match('#^/v1/control/result/([^/]+)$#',$path,$m)){
 $pc=$auth->pc(); $b=Util::jsonBody(); $status=(string)($b['status']??'ERROR'); if(!in_array($status,['DONE','ERROR'],true))Util::error('INVALID_STATUS',400);
 $encoded=json_encode($b['result']??new stdClass(),JSON_UNESCAPED_SLASHES); if($encoded===false||strlen($encoded)>12*1024*1024)Util::error('RESULT_TOO_LARGE',413);
 $n=$db->exec("UPDATE remote_commands SET status=?,result=?,completed_at=NOW() WHERE id=? AND pc_id=? AND status='RUNNING'",[$status,$encoded,$m[1],$pc['id']]); if($n!==1)Util::error('NOT_FOUND_OR_COMPLETED',409); Util::out(['ok'=>true]);
}
Util::error('ROUTE_NOT_FOUND',404);
