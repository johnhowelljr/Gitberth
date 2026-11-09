import { Controller, Get, Module } from "@nestjs/common";

@Controller()
export class AppController {
  @Get()
  hello(): string {
    return "Hello from NestJS";
  }
}

@Module({ controllers: [AppController] })
export class AppModule {}
