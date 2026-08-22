<?php
declare(strict_types=1);
session_start();
$config=require dirname(__DIR__).'/config.php';
foreach(['Util','Database'] as $f) require dirname(__DIR__).'/src/'.$f.'.php';
$db=new Database($config['db']);

function paired(Database $db,string $pc,string $dev):bool{return(bool)$db->one("SELECT id FROM pc_device_pairings WHERE pc_id=? AND device_id=? AND status='ACTIVE'",[$pc,$dev]);}
function detectMime(string $path):string{
    if(class_exists('finfo')){
        try{$f=new finfo(FILEINFO_MIME_TYPE);$m=$f->file($path);if(is_string($m)&&$m!=='')return $m;}catch(Throwable $e){}
    }
    if(function_exists('mime_content_type')){
        try{$m=mime_content_type($path);if(is_string($m)&&$m!=='')return $m;}catch(Throwable $e){}
    }
    return 'application/octet-stream';
}
function uploadErrorText(int $code):string{
    return match($code){
        UPLOAD_ERR_INI_SIZE=>'File exceeds PHP upload_max_filesize.',
        UPLOAD_ERR_FORM_SIZE=>'File exceeds form upload limit.',
        UPLOAD_ERR_PARTIAL=>'File upload was interrupted.',
        UPLOAD_ERR_NO_FILE=>'No file selected.',
        UPLOAD_ERR_NO_TMP_DIR=>'Server temporary upload directory is missing.',
        UPLOAD_ERR_CANT_WRITE=>'Server could not write the uploaded file.',
        UPLOAD_ERR_EXTENSION=>'A PHP extension stopped the upload.',
        default=>'Upload failed (code '.$code.').'
    };
}

$pc=(string)($_GET['pc']??$_SESSION['transfer_pc']??'');
$mode=(string)($_GET['mode']??$_SESSION['transfer_mode']??'pc');
$token=(string)($_GET['token']??'');
if($token!==''&&$pc!==''){
    $hash=hash('sha256',$token);
    if($mode==='iphone'){
        $d=$db->one('SELECT id FROM devices WHERE token_hash=? AND revoked_globally_at IS NULL',[$hash]);
        if($d&&paired($db,$pc,$d['id'])){$_SESSION['transfer_ok']=true;$_SESSION['transfer_pc']=$pc;$_SESSION['transfer_mode']='iphone';$_SESSION['transfer_device']=$d['id'];header('Location: transfer.php');exit;}
    }else{
        $p=$db->one('SELECT id FROM pcs WHERE id=? AND token_hash=?',[$pc,$hash]);
        if($p){$_SESSION['transfer_ok']=true;$_SESSION['transfer_pc']=$pc;$_SESSION['transfer_mode']='pc';header('Location: transfer.php');exit;}
    }
}
if(empty($_SESSION['transfer_ok'])||!$pc){http_response_code(401);?><!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{font:16px system-ui;background:#111;color:#eee;padding:30px}input,button{padding:12px;margin:6px;background:#222;color:#fff;border:1px solid #555;border-radius:8px}</style><h2>FaceUnlock File Upload</h2><p>Open this page from FaceUnlock Agent or the iPhone app.</p><?php exit;}

$direction=$mode==='iphone'?'IPHONE_TO_PC':'PC_TO_IPHONE';
$dir=dirname(__DIR__).'/storage/transfers';
if(!is_dir($dir)&&!mkdir($dir,0700,true)&&!is_dir($dir))throw new RuntimeException('Cannot create transfer storage directory');
$message='';$errorMessage='';
if($_SERVER['REQUEST_METHOD']==='POST'){
    if(empty($_FILES['files'])){$errorMessage='No file data received. Check PHP post_max_size and upload_max_filesize.';}
    else{
        $names=$_FILES['files']['name'];if(!is_array($names))$names=[$names];$saved=0;$errors=[];
        foreach($names as $i=>$name){
            $tmp=is_array($_FILES['files']['tmp_name'])?$_FILES['files']['tmp_name'][$i]:$_FILES['files']['tmp_name'];
            $err=(int)(is_array($_FILES['files']['error'])?$_FILES['files']['error'][$i]:$_FILES['files']['error']);
            if($err!==UPLOAD_ERR_OK){$errors[]=uploadErrorText($err);continue;}
            if(!is_uploaded_file($tmp)){$errors[]='Temporary upload is invalid.';continue;}
            $safe=basename((string)$name);if($safe==='')$safe='upload.bin';
            $id=bin2hex(random_bytes(16));$stored=$id.'.bin';$target=$dir.'/'.$stored;
            if(!move_uploaded_file($tmp,$target)){$errors[]='Could not move '.$safe.' into transfer storage.';continue;}
            $size=(int)(filesize($target)?:0);$mime=detectMime($target);$device=$mode==='iphone'?($_SESSION['transfer_device']??null):null;
            try{$db->exec("INSERT INTO transfer_files(id,pc_id,device_id,direction,original_name,stored_name,size_bytes,mime_type)VALUES(?,?,?,?,?,?,?,?)",[$id,$pc,$device,$direction,$safe,$stored,$size,$mime]);$saved++;}
            catch(Throwable $e){@unlink($target);$errors[]='Database could not register '.$safe.'.';}
        }
        if($saved>0)$message=$saved.' file(s) uploaded successfully.';
        if($errors)$errorMessage=implode(' ',array_unique($errors));
    }
}
$isIphone=$mode==='iphone';
?><!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><title>FaceUnlock Upload</title><style>body{margin:0;background:#111315;color:#eee;font:15px system-ui}.box{max-width:900px;margin:30px auto;background:#191b1e;border:1px solid #45484d;border-radius:14px;padding:22px}.drop{border:2px dashed #555;border-radius:12px;min-height:330px;display:flex;align-items:center;justify-content:center;flex-direction:column;text-align:center}.green{background:#169b45;color:white;border:0;border-radius:7px;padding:11px 18px;font-weight:600}.green:disabled{opacity:.55}.pick{margin:18px;max-width:90%}.hint{color:#999;font-size:26px}.ok{color:#69d58a}.err{color:#ff7b7b}.small{color:#999;font-size:13px}.state{min-height:24px;font-weight:600;color:#8fc5ff}</style></head><body><div class="box"><h2>Upload File</h2><?php if($message):?><p class="ok"><?=htmlspecialchars($message)?></p><?php endif?><?php if($errorMessage):?><p class="err"><?=htmlspecialchars($errorMessage)?></p><?php endif?><form id="uploadForm" method="post" enctype="multipart/form-data"><div class="drop"><div class="hint"><?= $isIphone?'Choose a file from iPhone':'Choose or drag files here' ?></div><input class="pick" id="files" type="file" name="files[]" <?= $isIphone?'':'multiple' ?>><div id="uploadState" class="state"></div><button id="submitBtn" class="green" type="submit"><?= $isIphone?'Upload selected file':'Confirm Upload' ?></button><p class="small">PHP limit: <?=htmlspecialchars((string)ini_get('upload_max_filesize'))?> per file, <?=htmlspecialchars((string)ini_get('post_max_size'))?> per request</p></div></form><p><?= $isIphone?'Choose a file once. FaceUnlock will start uploading it automatically.':'Files stay here until the iPhone downloads or deletes them.' ?></p></div>
<script>
(function(){
 const form=document.getElementById('uploadForm'), input=document.getElementById('files'), state=document.getElementById('uploadState'), btn=document.getElementById('submitBtn');
 let submitting=false;
 function beginUpload(){
   if(submitting) return;
   if(!input.files || input.files.length===0){state.textContent='No file selected.';return;}
   submitting=true;
   state.textContent='Uploading '+input.files[0].name+'…';
   btn.disabled=true; input.disabled=false;
   requestAnimationFrame(function(){form.submit();});
 }
 <?php if($isIphone): ?>
 input.addEventListener('change',function(){
   if(!input.files || input.files.length===0){state.textContent='Selection cancelled.';return;}
   state.textContent='Preparing '+input.files[0].name+'…';
   setTimeout(beginUpload,80);
 });
 <?php endif; ?>
 form.addEventListener('submit',function(e){
   if(submitting) return;
   if(!input.files || input.files.length===0){e.preventDefault();state.textContent='Choose a file first.';return;}
   submitting=true;state.textContent='Uploading…';btn.disabled=true;
 });
 window.addEventListener('pageshow',function(){if(submitting){submitting=false;btn.disabled=false;}});
})();
</script></body></html>
