<?php
declare(strict_types=1);
$config=require dirname(__DIR__).'/config.php'; foreach(['Util','Database','Migrator'] as $f) require dirname(__DIR__).'/src/'.$f.'.php'; (new Migrator(new Database($config['db'])))->migrate(); echo "FaceUnlock migrations applied\n";
