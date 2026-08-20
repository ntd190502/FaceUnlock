<?php
declare(strict_types=1);
final class RateLimiter {
    public function __construct(private Database $db,private AuditLog $audit,private array $rules) {}
    public function check(string $bucket,?string $identity=null): void {
        $r=$this->rules[$bucket]??['limit'=>60,'window'=>60]; $key=hash('sha256',$bucket.'|'.($identity?:Util::clientIpHash()));
        $cutoff=date('Y-m-d H:i:s',time()-(int)$r['window']); $n=(int)($this->db->one('SELECT COUNT(*) n FROM rate_limit_events WHERE bucket_hash=? AND bucket=? AND created_at>=?',[$key,$bucket,$cutoff])['n']??0);
        if($n>=(int)$r['limit']) {$this->audit->write('RATE_LIMITED','DENIED',null,null,null,['bucket'=>$bucket]); Util::error('RATE_LIMITED',429);}
        $this->db->exec('INSERT INTO rate_limit_events(bucket_hash,bucket) VALUES(?,?)',[$key,$bucket]);
    }
}
