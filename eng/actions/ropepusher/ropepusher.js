// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

function RopePusherException(message, postToGitHub = true) {
  this.message = message;
  this.postToGitHub = postToGitHub;
}

async function run() {
  const util = require("util");
  const jsExec = util.promisify(require("child_process").exec);

  console.log("Installing npm dependencies");
  const { stdout, stderr } = await jsExec("npm install @actions/core @actions/github @actions/exec");
  console.log("npm-install stderr:\n\n" + stderr);
  console.log("npm-install stdout:\n\n" + stdout);
  console.log("Finished installing npm dependencies");

  const core = require("@actions/core");
  const github = require("@actions/github");
  const exec = require("@actions/exec");

  const repo_owner = github.context.payload.repository.owner.login;
  const repo_name = github.context.payload.repository.name;
  const pr_number = github.context.payload.issue.number;
  const comment_user = github.context.payload.comment.user.login;

  let octokit = github.getOctokit(core.getInput("auth_token", { required: true }));
  let retries = core.getInput("retries", { required: true });

  try {
    // verify the comment user is a repo collaborator
    try {
      await octokit.rest.repos.checkCollaborator({
        owner: repo_owner,
        repo: repo_name,
        username: comment_user
      });
      console.log(`Verified ${comment_user} is a repo collaborator.`);
    } catch (error) {
      console.log(error);
      throw new RopePusherException(`Error: @${comment_user} is not a repo collaborator, using RopePusher is not allowed.`);
    }

    console.log("RopePusher `Hello, World!`");
    const hello_worldMessage = "`Hello, World!` from RopePusher";
    await octokit.rest.issues.createComment({
      owner: repo_owner,
      repo: repo_name,
      issue_number: pr_number,
      body: hello_worldMessage
    });
  }
  catch (error) {
    core.setFailed(error);

    if (error.postToGitHub === undefined || error.postToGitHub == true) {
      // post failure to GitHub comment
      const unknown_error_body = `@${comment_user} an error occurred while executing RopePusher, please check the run log for details!\n\n${error.message}`;
      await octokit.rest.issues.createComment({
        owner: repo_owner,
        repo: repo_name,
        issue_number: pr_number,
        body: unknown_error_body
      });
    }
  }
}

run();
