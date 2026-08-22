<?php
$uri=parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH);$file=__DIR__.$uri;if($uri!=='/'&&is_file($file))return false;if(str_starts_with((string)$uri,'/v1/control/'))require __DIR__.'/control.php';elseif(str_starts_with((string)$uri,'/v1/files/'))require __DIR__.'/files.php';else require __DIR__.'/index.php';
