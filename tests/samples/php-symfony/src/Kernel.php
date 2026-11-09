<?php

namespace App;

use Symfony\Bundle\FrameworkBundle\Kernel\MicroKernelTrait;
use Symfony\Component\DependencyInjection\Loader\Configurator\ContainerConfigurator;
use Symfony\Component\HttpFoundation\Response;
use Symfony\Component\HttpKernel\Kernel as BaseKernel;
use Symfony\Component\Routing\Attribute\Route;

// The micro kernel: FrameworkBundle only, its settings here rather than in config/, and the kernel's own route.
class Kernel extends BaseKernel
{
    use MicroKernelTrait;

    protected function configureContainer(ContainerConfigurator $container): void
    {
        $container->extension('framework', ['secret' => '%env(APP_SECRET)%']);
    }

    #[Route('/')]
    public function hello(): Response
    {
        return new Response('Hello from Symfony');
    }
}
