const path = require('path');

// SQLite, in .tmp/ next to the app; Strapi creates the folder and the database on its first start.
module.exports = ({ env }) => ({
  connection: {
    client: 'sqlite',
    connection: {
      filename: path.join(__dirname, '..', env('DATABASE_FILENAME', '.tmp/data.db')),
    },
    useNullAsDefault: true,
  },
});
