import { Component } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';

@Component({
  selector: 'app-root',
  template: '<h1>Hello from Angular</h1>',
})
class App {}

bootstrapApplication(App).catch((err) => console.error(err));
