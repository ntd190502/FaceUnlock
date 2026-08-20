<?php
declare(strict_types=1);
final class AuditLog {
    public function __construct(private Database $db) {}
    public function write(string $event, string $result, ?string $pcId=null, ?string $deviceId=null, ?string $requestId=null, array $metadata=[]): void {
        unset($metadata['token'],$metadata['approval_token'],$metadata['signature'],$metadata['private_key']);
        try {$this->db->exec('INSERT INTO security_audit_log(event,result,pc_id,device_id,request_id,ip_hash,metadata_json) VALUES(?,?,?,?,?,?,?)',[$event,$result,$pcId,$deviceId,$requestId,Util::clientIpHash(),json_encode($metadata,JSON_UNESCAPED_SLASHES)]);} catch (Throwable) {}
    }
}
