(function () {
  // Pages opened from this computer talk to the local backend (dotnet run on port 5294);
  // everywhere else they use the live API.
  var host = window.location.hostname;
  var isLocal = window.location.protocol === 'file:' || host === 'localhost' || host === '127.0.0.1';
  var localApi = window.location.port === '5294' ? '' : 'http://localhost:5294';

  window.GreenCycleConfig = {
    apiBaseUrl: isLocal ? localApi : "https://greencycleplatform.com"
  };
})();
