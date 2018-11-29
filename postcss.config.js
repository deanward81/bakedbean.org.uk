const purgecss = require('@fullhuman/postcss-purgecss')
const url = require('postcss-url')

module.exports = {
    plugins: [
        purgecss({
            content: ['./**/*.html', './config.toml']
        }),
        url({
            url: 'inline',
            basePath: ['./assets/css']
        })
    ]
}